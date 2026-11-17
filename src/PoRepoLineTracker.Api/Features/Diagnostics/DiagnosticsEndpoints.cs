using System.Reflection;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

internal static class DiagnosticsEndpoints
{
    /// <summary>
    /// Plain-English purpose for every registered health check, keyed by registration name. The
    /// checks' own descriptions are NOT passed through: they name tables and, for Key Vault, used
    /// to carry the vault URI, and this endpoint is anonymous.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Purpose, bool IsStorage)> Known = new()
    {
        ["azure-table-storage"] = ("Azure Table Storage", "Stores your tracked repositories, line-count history and settings.", true),
        ["key-vault"] = ("Azure Key Vault", "Holds the app's secrets, such as the GitHub sign-in keys.", false),
        ["github-api"] = ("GitHub API", "Reads your repositories and their commit history.", false),
    };

    private static readonly string Version = typeof(DiagnosticsEndpoints).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildStamp")?.Value ?? "dev";

    internal static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // /health is served by app.MapHealthChecks("/health") in Program.cs. This is the same
        // registry shaped for the /diag page. Anonymous by the fleet /diag standard (2026-11-17):
        // the page must work signed-out, so the payload is limited to statuses and fixed text.
        endpoints.MapGet("/api/diag", GetAsync)
            .WithTags("Diagnostics")
            .WithName("ApiDiag")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Health);
    }

    private static async Task<IResult> GetAsync(
        IConfiguration configuration, IWebHostEnvironment env, HealthCheckService healthChecks,
        IAuthenticationSchemeProvider schemes, CancellationToken ct)
    {
        HealthReport report;
        try
        {
            report = await healthChecks.CheckHealthAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Health check execution failed during diagnostics request");
            report = new HealthReport(new Dictionary<string, HealthReportEntry>(), HealthStatus.Unhealthy, TimeSpan.Zero);
        }

        var response = new DiagnosticsResponse
        {
            Status = report.Status.ToString(),
            Environment = env.EnvironmentName,
            Version = Version,
            CheckedAtUtc = DateTime.UtcNow,
        };

        foreach (var (key, entry) in report.Entries)
        {
            var (name, purpose, isStorage) = Known.TryGetValue(key, out var known) ? known : (key, "Dependency check.", false);
            (isStorage ? response.Storage : response.Services).Add(new DiagnosticsCheck
            {
                Name = name,
                Purpose = purpose,
                Status = entry.Status.ToString(),
                Note = NoteFor(key, entry.Status),
                LatencyMs = Math.Round(entry.Duration.TotalMilliseconds, 1),
                ErrorType = entry.Exception?.GetType().Name,
            });
        }

        // Configuration-only rows: no probe, so no latency, and they do not change /health.
        var signIn = await schemes.GetSchemeAsync(GitHubAuthenticationDefaults.AuthenticationScheme) is not null;
        response.Services.Add(new DiagnosticsCheck
        {
            Name = "GitHub sign-in",
            Purpose = "Signs you in with your GitHub account (OAuth).",
            Status = signIn ? "Healthy" : "Degraded",
            Note = signIn ? null : "Not configured on this server; GitHub sign-in is unavailable.",
        });

        var telemetry = TelemetrySettings.IsAppInsightsConfigured(configuration) || TelemetrySettings.IsOtlpConfigured(configuration);
        response.Services.Add(new DiagnosticsCheck
        {
            Name = "Telemetry export",
            Purpose = "Sends traces and metrics to Application Insights or an OTLP collector. Optional.",
            Status = telemetry ? "Healthy" : "Degraded",
            Note = telemetry ? null : "Not configured; the app runs without it.",
        });

        return Results.Ok(response);
    }

    private static string? NoteFor(string key, HealthStatus status) => (key, status) switch
    {
        (_, HealthStatus.Healthy) => null,
        ("key-vault", _) => "Not configured; secrets come from local settings or environment variables.",
        ("github-api", _) => "Unreachable, rate-limited or missing a credential; cached reads still work.",
        ("azure-table-storage", _) => "Storage could not be reached; nothing can be loaded or saved.",
        _ => "Check reported a problem.",
    };
}
