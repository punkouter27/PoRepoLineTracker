using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace PoRepoLineTracker.Api.Extensions;

public static class TelemetryServiceExtensions
{
    public static IServiceCollection AddTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        // Connection-string resolution order:
        //   1. APPLICATIONINSIGHTS_CONNECTION_STRING  / ApplicationInsights:ConnectionString
        //   2. APPINSIGHTS_INSTRUMENTATIONKEY         / ApplicationInsights:InstrumentationKey
        //                                               (promoted to a connection string)
        //
        // The resolution lives in TelemetrySettings so the diagnostics endpoint can report exactly
        // what this method will do. It used to be inline here, and the client diagnostics view
        // reimplemented a subset of it — which is why an app configured through Key Vault showed
        // "Not configured".
        var aiCs = TelemetrySettings.ResolveAppInsightsConnectionString(configuration);

        // cloud_RoleName mapping: resolve the real assembly name via reflection so the
        // App Insights "cloud_RoleName" never falls back to "unknown_service:dotnet".
        var roleName = Assembly.GetEntryAssembly()?.GetName().Name
                       ?? AppTelemetry.SourceName;
        var roleVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                          ?? AppTelemetry.Version;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: roleName,
                serviceVersion: roleVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AppTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation();

                if (environment.IsDevelopment() &&
                    string.Equals(configuration[ConfigKeys.Telemetry.EnableConsoleExporters], "true", StringComparison.OrdinalIgnoreCase))
                    tracing.AddConsoleExporter();

                var otlpEndpoint = configuration[ConfigKeys.Telemetry.OtlpEndpoint];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                // Cost cap: drop the noisy ASP.NET hosting / HTTP client pre-aggregated meters
                // (~60% of this app's Log Analytics ingestion). Reversible via config.
                //
                // A Drop view, not a "remove meter" call: MeterProviderBuilder has no RemoveMeter
                // (the first attempt at this did not compile), and simply not calling the
                // Add*Instrumentation methods would not be enough either — UseAzureMonitor below
                // subscribes to the same meters on its own. A view applies to the whole provider.
                if (!configuration.GetValue(ConfigKeys.Telemetry.EnableAspNetCoreMeters, false))
                {
                    metrics.AddView(instrument =>
                        instrument.Meter.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                        || instrument.Meter.Name.StartsWith("System.Net.", StringComparison.Ordinal)
                            ? MetricStreamConfiguration.Drop
                            : null);
                }

                if (environment.IsDevelopment() &&
                    string.Equals(configuration[ConfigKeys.Telemetry.EnableConsoleExporters], "true", StringComparison.OrdinalIgnoreCase))
                    metrics.AddConsoleExporter();

                var otlpEndpoint = configuration[ConfigKeys.Telemetry.OtlpEndpoint];
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            });

        // Only register Azure Monitor when a connection string is configured.
        // Calling UseAzureMonitor() without a connection string throws at host startup.
        if (!string.IsNullOrWhiteSpace(aiCs))
        {
            services.AddOpenTelemetry()
                .UseAzureMonitor(o =>
                {
                    o.ConnectionString = aiCs;

                    // Live Metrics / QuickPulse disabled in Production to avoid bypassing adaptive sampling.
                    o.EnableLiveMetrics = !environment.IsProduction();

                    // Sampling profile: full fidelity (100%) in Dev/Test so nothing
                    // is dropped during debugging and E2E runs; capped to ~10% in Production.
                    // Note: the Azure Monitor OTel distro applies a single trace-level rate;
                    // RecordException above ensures exception detail is preserved on sampled-in
                    // traces. (Per-signal "exceptions always 100%" is not separately tunable in
                    // the OTel distro, unlike the legacy adaptive sampler.)
                    o.SamplingRatio = environment.IsProduction() ? 0.1f : 1.0f;
                });
        }

        return services;
    }
}
