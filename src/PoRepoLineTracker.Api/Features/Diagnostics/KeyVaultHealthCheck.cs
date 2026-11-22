using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoRepoLineTracker.Api.Features.Diagnostics;

/// <summary>
/// Health check for Azure Key Vault: a live call. Lists one page (one entry) of secret names with the app's own
/// credential, the same read the configuration provider does at startup, so it proves the vault answers AND the
/// managed identity may still read it. Off (see <see cref="DiagOff"/>) when <c>KeyVault:Uri</c> is unset (secrets come from local settings);
/// Degraded when the call fails (the app keeps the secrets it loaded at startup). The description never names the vault:
/// /health and /api/diag are anonymous.
/// </summary>
public sealed class KeyVaultHealthCheck(
    IConfiguration configuration,
    ILogger<KeyVaultHealthCheck> logger) : IHealthCheck
{
    // One credential for the process, so its token cache is reused between probes.
    private static readonly Lazy<DefaultAzureCredential> Credential = new(() => new DefaultAzureCredential());

    /// <summary>The live call. Replaceable so unit tests do not need a vault.</summary>
    internal Func<Uri, CancellationToken, Task> Probe { get; init; } = ListOneSecretNameAsync;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var uri = configuration[ConfigKeys.KeyVault.Uri];
        if (string.IsNullOrWhiteSpace(uri))
        {
            return DiagOff.Result("KeyVault:Uri is not configured; secrets come from local settings or environment variables");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Probe(new Uri(uri), timeout.Token);
            return HealthCheckResult.Healthy("Key Vault answered a live call");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Key Vault health check failed");
            return HealthCheckResult.Degraded("Key Vault did not answer", ex);
        }
    }

    private static async Task ListOneSecretNameAsync(Uri vault, CancellationToken ct)
    {
        var client = new SecretClient(vault, Credential.Value);
        await foreach (var _ in client.GetPropertiesOfSecretsAsync(ct).AsPages(pageSizeHint: 1).WithCancellation(ct))
        {
            return;
        }
    }
}
