using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

/// <summary>
/// Health check for Azure Key Vault availability. Configuration-only:
/// Healthy when <c>KeyVault:Uri</c> is set, Degraded otherwise.
///
/// <para>The actual credential probe happens at startup inside <c>Program.cs</c> (via
/// <c>DefaultAzureCredential</c>) and surfaces through <c>/diagnostics</c> as
/// "Configured / Not configured". A runtime credential round-trip on every health check would
/// either add a network call per probe or be cached and lie about its freshness — neither is
/// worth the cost when the registration shape is the contract: a missing URI is the single
/// thing that means "this app cannot read its secrets".</para>
/// </summary>
public sealed class KeyVaultHealthCheck(
    IConfiguration configuration,
    ILogger<KeyVaultHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var uri = configuration[ConfigKeys.KeyVault.Uri];
        var result = string.IsNullOrWhiteSpace(uri)
            ? HealthCheckResult.Degraded("KeyVault:Uri is not configured — secrets fall back to user-secrets / environment variables")
            : HealthCheckResult.Healthy($"KeyVault is configured at {uri}");

        if (result.Status == HealthStatus.Degraded)
        {
            logger.LogDebug("Health check: {Description}", result.Description);
        }

        return Task.FromResult(result);
    }
}
