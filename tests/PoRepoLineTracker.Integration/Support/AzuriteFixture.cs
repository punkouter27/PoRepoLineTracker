using Testcontainers.Azurite;
using Xunit;

namespace PoRepoLineTracker.Integration;

/// <summary>
/// Holds the Testcontainers-managed Azurite connection string for the test session.
/// <see cref="CustomWebApplicationFactory"/> reads this so the integration tier talks to a
/// real, ephemeral Azurite container (Table Storage) with zero manual cleanup.
/// </summary>
public static class AzuriteState
{
    public static string? ConnectionString { get; set; }
}

/// <summary>
/// Collection fixture that owns the Azurite container lifecycle via Testcontainers — the
/// container is started once per test session and torn down automatically (no manual
/// docker compose / cleanup). If Docker is unavailable the fixture degrades gracefully:
/// the factory then falls back to a locally-running Azurite probe, and finally to mocks.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    // Built inside the try below, not in a field initializer: Build() itself throws when no
    // Docker endpoint is reachable, which failed every test in the tier from the constructor —
    // before the "degrades gracefully" catch could run.
    private AzuriteContainer? _container;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
                .Build();
            await _container.StartAsync();
            AzuriteState.ConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            // Docker/Testcontainers unavailable — leave the connection string null so the
            // factory falls back. Never fail the whole tier just because Docker is absent.
            Console.WriteLine($"[AzuriteFixture] Testcontainers Azurite unavailable, falling back: {ex.Message}");
            AzuriteState.ConnectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        AzuriteState.ConnectionString = null;
        try { if (_container is not null) await _container.DisposeAsync(); }
        catch { /* best-effort teardown */ }
    }
}
