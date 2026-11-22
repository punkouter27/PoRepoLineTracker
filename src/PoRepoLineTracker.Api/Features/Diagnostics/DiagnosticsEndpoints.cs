using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRepoLineTracker.Api.Platform;
using Serilog;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

internal static class DiagnosticsEndpoints
{
    /// <summary>
    /// Plain-English purpose for every registered health check, keyed by registration name. The
    /// checks' own descriptions are NOT passed through: they name tables and, for Key Vault, used
    /// to carry the vault URI, and this endpoint is anonymous.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Purpose, bool IsStorage)> Known = new()
    {
        ["azure-table-storage"] = ("Azure Table Storage", "Stores your tracked repositories, line-count history and settings.", true),
        ["key-vault"] = ("Azure Key Vault", "Holds the app's secrets, such as the GitHub sign-in keys.", false),
        ["github-api"] = ("GitHub API", "Reads your repositories and their commit history.", false),
        ["github-sign-in"] = ("GitHub sign-in", "Signs you in with your GitHub account (OAuth).", false),
        ["telemetry"] = ("Telemetry export", "Sends traces and metrics to Application Insights or an OTLP collector. Optional.", false),
    };

    internal static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // /health is served by app.MapHealthChecks("/health") in Program.cs. This is the same
        // registry shaped for the /diag page (the Po /diag contract: DiagReportDto). Anonymous: the
        // page must work signed-out, so the payload is limited to statuses and fixed text.
        endpoints.MapGet("/api/diag", GetAsync)
            .WithTags("Diagnostics")
            .WithName("ApiDiag")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Health);
    }

    private static async Task<IResult> GetAsync(
        IWebHostEnvironment env, HealthCheckService healthChecks, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var checks = new List<DiagCheckDto>
        {
            // The app's own server: it is answering this request, so it is up. Its latency is the time the request
            // took to reach this handler (the /diag page shows its own round trip instead).
            new("server", "api", "Server", "Healthy", "The app's own API, which this page just called.",
                "Answered this request.", Math.Round((DateTime.UtcNow - (Activity.Current?.StartTimeUtc ?? DateTime.UtcNow)).TotalMilliseconds, 1), null),
        };

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

        foreach (var (key, entry) in report.Entries)
        {
            var (label, purpose, isStorage) = Known.TryGetValue(key, out var known) ? known : (key, "Dependency check.", false);
            // Off: the dependency is switched off on purpose here (not configured). Neutral, no call made, and it does
            // not count toward the overall status.
            if (DiagOff.IsOff(entry))
            {
                checks.Add(new DiagCheckDto(
                    key, isStorage ? "data" : "service", label, "Off", purpose,
                    "Switched off in this environment; no call made.", 0, null));
                continue;
            }

            var problem = NoteFor(key, entry.Status);
            if (problem is not null && entry.Exception is not null) problem += $" ({entry.Exception.GetType().Name})";
            checks.Add(new DiagCheckDto(
                key, isStorage ? "data" : "service", label, entry.Status.ToString(), purpose,
                "Checked with a live call.", Math.Round(entry.Duration.TotalMilliseconds, 1), problem));
        }

        var status = checks.Any(c => c.Status == "Unhealthy") ? "Unhealthy"
            : checks.Any(c => c.Status == "Degraded") ? "Degraded"
            : "Healthy";

        return Results.Ok(new DiagReportDto(
            status, env.EnvironmentName, PoPlatform.Version, DateTimeOffset.UtcNow,
            Math.Round(watch.Elapsed.TotalMilliseconds, 1), [.. checks]));
    }

    private static string? NoteFor(string key, HealthStatus status) => (key, status) switch
    {
        (_, HealthStatus.Healthy) => null,
        ("key-vault", _) => "Not configured or did not answer; the app keeps the secrets it loaded at startup.",
        ("github-api", _) => "Unreachable, rate-limited or missing a credential; cached reads still work.",
        ("github-sign-in", HealthStatus.Unhealthy) => "GitHub rejected the app's sign-in credentials; nobody can sign in.",
        ("github-sign-in", _) => "Not configured or GitHub did not answer; sign-in may be unavailable.",
        ("telemetry", _) => "Not configured or not reachable; the app runs without it.",
        ("azure-table-storage", _) => "Storage could not be reached; nothing can be loaded or saved.",
        _ => "Check reported a problem.",
    };
}
