using System.Text.Json;
using AspNet.Security.OAuth.GitHub;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

/// <summary>
/// GitHub sign-in, checked live: exchanges a made-up code at GitHub's token endpoint with the app's real client id and
/// secret. GitHub answers <c>bad_verification_code</c> when it accepts the app's credentials and
/// <c>incorrect_client_credentials</c> when it does not, so this proves both that GitHub answers and that the OAuth
/// app still exists. Costs nothing and signs nobody in. Descriptions are fixed sentences (the endpoint is anonymous).
/// </summary>
public sealed class GitHubSignInHealthCheck(
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    IOptionsMonitor<GitHubAuthenticationOptions> options,
    ILogger<GitHubSignInHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var github = options.Get(GitHubAuthenticationDefaults.AuthenticationScheme);
        if (string.IsNullOrEmpty(github.ClientId) || string.IsNullOrEmpty(github.ClientSecret))
        {
            // Development and Test sign in with the dev persona, so no OAuth app there is on purpose. Production needs it.
            return environment.IsProduction()
                ? HealthCheckResult.Degraded("GitHub sign-in is not configured on this server")
                : DiagOff.Result("GitHub sign-in is not configured in this environment");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, github.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = github.ClientId,
                    ["client_secret"] = github.ClientSecret,
                    ["code"] = "po-diag-probe",
                }),
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClientFactory.CreateClient(DiagProbe.ClientName).SendAsync(request, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            return error switch
            {
                "bad_verification_code" => HealthCheckResult.Healthy("GitHub accepted the app's sign-in credentials"),
                "incorrect_client_credentials" => HealthCheckResult.Unhealthy("GitHub rejected the app's sign-in credentials"),
                _ => HealthCheckResult.Degraded("GitHub answered the sign-in check unexpectedly"),
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub sign-in health check failed");
            return HealthCheckResult.Degraded("GitHub's sign-in endpoint did not answer", ex);
        }
    }
}

/// <summary>
/// Telemetry export, checked live: one request to the Application Insights ingestion endpoint (or the OTLP collector)
/// the exporter sends to. Any HTTP answer means the exporter can reach it. Degraded when nothing is configured: the
/// app runs without it.
/// </summary>
public sealed class TelemetryExportHealthCheck(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TelemetryExportHealthCheck> logger) : IHealthCheck
{
    private const string DefaultIngestion = "https://dc.services.visualstudio.com/";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var target = IngestionEndpoint(TelemetrySettings.ResolveAppInsightsConnectionString(configuration))
                     ?? TelemetrySettings.ResolveOtlpEndpoint(configuration);
        if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return DiagOff.Result("Telemetry export is not configured");
        }

        try
        {
            using var response = await httpClientFactory.CreateClient(DiagProbe.ClientName).GetAsync(uri, cancellationToken);
            return HealthCheckResult.Healthy("The telemetry endpoint answered");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Telemetry export health check failed");
            return HealthCheckResult.Degraded("The telemetry endpoint did not answer", ex);
        }
    }

    /// <summary>The ingestion endpoint named in an App Insights connection string, or the global default.</summary>
    internal static string? IngestionEndpoint(string? connectionString) => connectionString is null
        ? null
        : connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(part => part.Split('=', 2))
              .Where(kv => kv.Length == 2 && kv[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
              .Select(kv => kv[1])
              .FirstOrDefault() ?? DefaultIngestion;
}

/// <summary>The plain HttpClient the live service probes use: short timeout, no retries (a probe reports, it does not insist).</summary>
internal static class DiagProbe
{
    internal const string ClientName = "DiagProbe";
}

/// <summary>
/// "Off": a dependency switched off on purpose in this environment (its setting is absent). The check returns Healthy
/// (so /health stays green) with this marker, and /api/diag shows it as the neutral status "Off".
/// </summary>
internal static class DiagOff
{
    private const string Key = "off";

    internal static HealthCheckResult Result(string description) =>
        HealthCheckResult.Healthy(description, new Dictionary<string, object> { [Key] = true });

    internal static bool IsOff(HealthReportEntry entry) => entry.Data.ContainsKey(Key);
}
