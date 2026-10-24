using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.ComponentModel.DataAnnotations;
using Serilog;

namespace PoRepoLineTracker.API.Features.Diagnostics;

internal static class DiagnosticsEndpoints
{
    internal static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // /health is served by the registered IHealthCheck pipeline via app.MapHealthChecks("/health")
        // in Program.cs — no custom implementation needed here.

        // The single server-side diagnostics surface. The Blazor client keeps a /diag PAGE route
        // (Pages/Diagnostics.razor) whose data comes from here, so a browser never needs a
        // content-negotiating server route on the same path.
        var diagnostics = endpoints.MapGroup("/api/diagnostics")
            .WithTags("Diagnostics")
            .RequireAuthorization();

        diagnostics.MapGet("/", async (IConfiguration configuration, IWebHostEnvironment env, HealthCheckService healthChecks) =>
        {
            return await MapDiagnosticsData(configuration, env, healthChecks);
        })
        .WithName("ApiDiagnostics");
    }

    private static async Task<IResult> MapDiagnosticsData(IConfiguration configuration, IWebHostEnvironment env, HealthCheckService healthChecks)
    {
        HealthReport report;
        try
        {
            report = await healthChecks.CheckHealthAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Health check execution failed during diagnostics request");
            report = new HealthReport(
                new Dictionary<string, HealthReportEntry>
                {
                    ["health_check_error"] = new HealthReportEntry(
                        HealthStatus.Unhealthy, ex.Message, TimeSpan.Zero, ex, null)
                },
                HealthStatus.Unhealthy,
                TimeSpan.Zero);
        }

        // Telemetry status comes from TelemetrySettings, which is the same resolver AddTelemetry
        // uses. Checking ConfigKeys.Telemetry.AppInsightsConnectionString directly — as this did —
        // sees only the environment-variable form, so a connection string supplied through
        // appsettings, user-secrets or Key Vault (the only places it is allowed to live, since it
        // must never be committed) was reported as "Not configured" while telemetry was in fact
        // exporting.
        var appInsightsConfigured = TelemetrySettings.IsAppInsightsConfigured(configuration);
        var otlpConfigured = TelemetrySettings.IsOtlpConfigured(configuration);

        int configuredCount = 0;
        if (!string.IsNullOrEmpty(configuration[ConfigKeys.KeyVault.Uri])) configuredCount++;
        if (!string.IsNullOrEmpty(configuration[ConfigKeys.AzureTableStorage.ServiceUrl]) || !string.IsNullOrEmpty(configuration[ConfigKeys.AzureTableStorage.ConnectionString])) configuredCount++;
        if (appInsightsConfigured) configuredCount++;
        if (!string.IsNullOrEmpty(configuration[ConfigKeys.GitHub.ClientId])) configuredCount++;
        if (!string.IsNullOrEmpty(configuration[ConfigKeys.GitHub.Pat])) configuredCount++;
        if (otlpConfigured) configuredCount++;

        var externalConnections = new ExternalConnectionsData
        {
            Azure =
            [
                new ExternalConnectionInfo { Name = "Azure Key Vault", Type = "Secret Storage", Status = !string.IsNullOrEmpty(configuration[ConfigKeys.KeyVault.Uri]) ? "Configured" : "Not configured", Purpose = "Securely stores secrets" },
                new ExternalConnectionInfo { Name = "Azure Table Storage", Type = "Data Storage", Status = !string.IsNullOrEmpty(configuration[ConfigKeys.AzureTableStorage.ServiceUrl]) || !string.IsNullOrEmpty(configuration[ConfigKeys.AzureTableStorage.ConnectionString]) ? "Configured" : "Not configured", Purpose = "Stores repository analysis data" },
                new ExternalConnectionInfo { Name = "Azure Application Insights", Type = "Telemetry", Status = appInsightsConfigured ? "Configured" : "Not configured", Purpose = "Performance monitoring" }
            ],
            GitHub =
            [
                new ExternalConnectionInfo { Name = "GitHub OAuth", Type = "Authentication", Status = !string.IsNullOrEmpty(configuration[ConfigKeys.GitHub.ClientId]) ? "Configured" : "Not configured", Purpose = "User authentication" },
                new ExternalConnectionInfo { Name = "GitHub REST API", Type = "External API", Status = !string.IsNullOrEmpty(configuration[ConfigKeys.GitHub.Pat]) ? "PAT Configured" : "Rate Limited", Purpose = "Repository data access" }
            ],
            OpenTelemetry =
            [
                new ExternalConnectionInfo { Name = "OTLP Exporter", Type = "Telemetry Export", Status = otlpConfigured ? "Configured" : "Not configured", Purpose = "Distributed tracing" }
            ]
        };

        return Results.Ok(new DiagnosticsResponse
        {
            Environment = env.EnvironmentName,
            Timestamp = DateTime.UtcNow,
            OverallHealth = report.Status.ToString(),
            ExternalConnections = externalConnections,
            Summary = new DiagnosticsSummary
            {
                TotalConnections = 6,
                ConfiguredCount = configuredCount,
                ApplicationPurpose = "Repository line tracking, line-count history, contributor statistics"
            }
        });
    }

}


