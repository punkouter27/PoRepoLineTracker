using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoRepoLineTracker.Api.Features.Diagnostics;

namespace PoRepoLineTracker.Unit.Diagnostics;

/// <summary>/health and /api/diag run the live probes at most once per <see cref="CachedHealthCheckService.Ttl"/>.</summary>
public class CachedHealthCheckServiceTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Probes_run_once_per_ttl_and_again_after_it()
    {
        var runs = 0;
        var time = new ManualTime();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
        services.AddHealthChecks().AddCheck("probe", () => { runs++; return HealthCheckResult.Healthy(); });
        CachedHealthCheckService.Decorate(services);
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        health.Should().BeOfType<CachedHealthCheckService>();
        (await health.CheckHealthAsync()).Status.Should().Be(HealthStatus.Healthy);
        await health.CheckHealthAsync();
        time.Now += TimeSpan.FromSeconds(59);
        await health.CheckHealthAsync();
        runs.Should().Be(1, "repeat calls inside the TTL reuse the cached report");
        ((CachedHealthCheckService)health).CheckedAt.Should().Be(time.Now - TimeSpan.FromSeconds(59));

        time.Now += TimeSpan.FromSeconds(2);
        await health.CheckHealthAsync();
        runs.Should().Be(2, "the report is refreshed once the TTL has passed");
    }
}
