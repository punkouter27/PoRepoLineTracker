using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.Api.Storage;

/// <summary>
/// The memo of per-commit code-health figures. See <see cref="CodeHealthSnapshotEntity"/> for why
/// it is keyed on the commit rather than on anything time-shaped.
/// </summary>
public interface ICodeHealthSnapshotStore
{
    /// <summary>
    /// Every memoised commit for one repository, keyed by SHA, in ONE query.
    ///
    /// <para>Deliberately not a "get one snapshot" method. Callers want figures for many commits —
    /// the trend asks for twenty-four — and a point read per SHA inside that loop is the exact
    /// pattern that had <c>CommitExistsAsync</c> deleted: one round-trip per commit, thousands of
    /// them on a large repository, to answer what a single partition scan already returns.</para>
    /// </summary>
    Task<Dictionary<string, CodeHealthSnapshotEntity>> GetByRepositoryAsync(RepositoryId repositoryId);

    /// <summary>Writes one commit's figures. Upsert, so a re-scored commit replaces cleanly.</summary>
    Task SaveAsync(CodeHealthSnapshotEntity snapshot);

    /// <summary>Drops a repository's memo — used when its commits are deleted, so rows cannot outlive them.</summary>
    Task DeleteForRepositoryAsync(RepositoryId repositoryId);
}

/// <inheritdoc />
public sealed class CodeHealthSnapshotStore : ICodeHealthSnapshotStore
{
    /// <summary>
    /// Bumped whenever the scoring arithmetic changes. A memo is only valid while the function it
    /// memoises is unchanged; without this, a chart would mix figures from two different formulas
    /// and show a step change that never happened in the code.
    /// </summary>
    public const int CurrentScoringVersion = 1;

    private readonly TableClient _table;
    private readonly ILogger<CodeHealthSnapshotStore> _logger;

    public CodeHealthSnapshotStore(
        TableServiceClient tableServiceClient,
        IConfiguration configuration,
        ILogger<CodeHealthSnapshotStore> logger)
    {
        var tableName = configuration[ConfigKeys.AzureTableStorage.CodeHealthSnapshotTableName]
            ?? "PoRepoLineTrackerCodeHealthSnapshots";

        _table = tableServiceClient.GetTableClient(tableName);
        _logger = logger;
    }

    public async Task<Dictionary<string, CodeHealthSnapshotEntity>> GetByRepositoryAsync(RepositoryId repositoryId)
    {
        var partition = repositoryId.ToString();
        var snapshots = new Dictionary<string, CodeHealthSnapshotEntity>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await _table.CreateIfNotExistsAsync();

            await foreach (var entity in _table.QueryAsync<CodeHealthSnapshotEntity>(e => e.PartitionKey == partition))
            {
                // A row written by older scoring logic is not comparable with a fresh one, so it is
                // treated as a miss and recomputed rather than plotted beside new figures.
                if (entity.ScoringVersion != CurrentScoringVersion) continue;

                snapshots[entity.RowKey] = entity;
            }
        }
        catch (RequestFailedException ex)
        {
            // The memo is an optimisation, never the source of truth. If it cannot be read the
            // caller recomputes — slower, but correct — so this must not fail the request.
            _logger.LogWarning(ex, "Could not read code-health snapshots for {RepositoryId}; recomputing", repositoryId);
        }

        return snapshots;
    }

    public async Task SaveAsync(CodeHealthSnapshotEntity snapshot)
    {
        try
        {
            await _table.CreateIfNotExistsAsync();
            snapshot.ScoringVersion = CurrentScoringVersion;
            await _table.UpsertEntityAsync(snapshot, TableUpdateMode.Replace);
        }
        catch (RequestFailedException ex)
        {
            // Failing to cache is not failing to answer. The figure was already computed and is
            // being returned; losing the write only costs time on the next request.
            _logger.LogWarning(ex, "Could not persist code-health snapshot for {RepositoryId} at {Sha}",
                snapshot.RepositoryId, snapshot.CommitSha);
        }
    }

    public async Task DeleteForRepositoryAsync(RepositoryId repositoryId)
    {
        var partition = repositoryId.ToString();

        try
        {
            await _table.CreateIfNotExistsAsync();

            var stale = new List<CodeHealthSnapshotEntity>();
            await foreach (var entity in _table.QueryAsync<CodeHealthSnapshotEntity>(e => e.PartitionKey == partition))
                stale.Add(entity);

            foreach (var entity in stale)
                await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Could not clear code-health snapshots for {RepositoryId}", repositoryId);
        }
    }
}
