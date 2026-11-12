using Microsoft.Extensions.Configuration;

namespace PoRepoLineTracker.Api.Telemetry;

/// <summary>
/// The one place that decides whether telemetry is configured, and from which key.
///
/// <para>This exists because the answer was previously computed twice and the two copies
/// disagreed. <c>TelemetryServiceExtensions</c> accepts an Application Insights connection string
/// from FOUR sources — the <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> environment key, the
/// <c>ApplicationInsights:ConnectionString</c> section, and an instrumentation key in either of
/// the same two shapes — while <c>DiagnosticsEndpoints</c> checked only the first. So an app
/// wired up through <c>appsettings</c> or Key Vault (which is where this project puts the value,
/// since a connection string must never be committed) reported <b>"Not configured"</b> on the
/// diagnostics page while happily exporting to App Insights.</para>
///
/// <para>A diagnostics page that disagrees with the running configuration is worse than no
/// diagnostics page: it is the screen you open precisely when you are trying to find out what is
/// switched on. Both callers now read the answer from here.</para>
/// </summary>
internal static class TelemetrySettings
{
    /// <summary>
    /// Resolves the Application Insights connection string using the same precedence
    /// <c>AddTelemetry</c> applies. Returns <c>null</c> when nothing is configured.
    /// </summary>
    internal static string? ResolveAppInsightsConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration[ConfigKeys.Telemetry.AppInsightsConnectionString]
                               ?? configuration[ConfigKeys.Telemetry.AppInsightsConnectionStringSection];

        if (!string.IsNullOrWhiteSpace(connectionString)) return connectionString;

        var instrumentationKey = configuration[ConfigKeys.Telemetry.AppInsightsInstrumentationKey]
                                 ?? configuration[ConfigKeys.Telemetry.AppInsightsInstrumentationKeySection];

        return string.IsNullOrWhiteSpace(instrumentationKey)
            ? null
            : $"InstrumentationKey={instrumentationKey}";
    }

    /// <summary>True when Azure Monitor export will actually be registered.</summary>
    internal static bool IsAppInsightsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(ResolveAppInsightsConnectionString(configuration));

    /// <summary>
    /// The OTLP collector endpoint, or <c>null</c>. Whitespace counts as unset — the exporter is
    /// only registered for a non-blank value, and `""` is what an unconfigured appsettings entry
    /// actually holds.
    /// </summary>
    internal static string? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var endpoint = configuration[ConfigKeys.Telemetry.OtlpEndpoint];
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
    }

    /// <summary>True when an OTLP exporter will actually be registered.</summary>
    internal static bool IsOtlpConfigured(IConfiguration configuration) =>
        ResolveOtlpEndpoint(configuration) is not null;
}
