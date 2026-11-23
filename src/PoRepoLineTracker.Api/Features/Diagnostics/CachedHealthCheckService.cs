using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

/// <summary>
/// Caches the full health report for <see cref="Ttl"/>, so /health and /api/diag (both anonymous) cost at most one
/// round of live probes (Table Storage, Key Vault, GitHub /rate_limit, the GitHub token endpoint, telemetry) per
/// minute however often they are hit. Concurrent callers during a refresh wait for that one refresh. Calls with a
/// predicate (a filtered subset) are not cached.
/// </summary>
internal sealed class CachedHealthCheckService(HealthCheckService inner, TimeProvider time) : HealthCheckService, IDisposable
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (HealthReport Report, DateTimeOffset At)? _last;

    /// <summary>When the report currently served was produced (null before the first check).</summary>
    internal DateTimeOffset? CheckedAt => _last?.At;

    public override async Task<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
    {
        if (predicate is not null) return await inner.CheckHealthAsync(predicate, cancellationToken);
        if (Fresh() is { } hit) return hit;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } again) return again;
            var report = await inner.CheckHealthAsync(null, cancellationToken);
            _last = (report, time.GetUtcNow());
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private HealthReport? Fresh() =>
        _last is { } last && time.GetUtcNow() - last.At < Ttl ? last.Report : null;

    /// <summary>Wraps the framework's HealthCheckService (registered by AddHealthChecks) in this cache.</summary>
    internal static void Decorate(IServiceCollection services)
    {
        var original = services.Last(d => d.ServiceType == typeof(HealthCheckService));
        var implementation = original.ImplementationType
            ?? throw new InvalidOperationException("HealthCheckService is expected to be registered by type.");
        services.Remove(original);
        services.AddSingleton(implementation);
        services.AddSingleton<HealthCheckService>(sp => new CachedHealthCheckService(
            (HealthCheckService)sp.GetRequiredService(implementation),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
    }
}
