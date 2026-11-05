using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Features.CodeHealth;

/// <summary>
/// Measures one repository's source at its most recent analysed commit.
/// </summary>
public record GetCodeHealthQuery(RepositoryId RepositoryId);

/// <summary>
/// <para><b>Why this reads the working clone rather than stored data.</b> Every other query in the
/// app answers from Azure Table Storage, because what it needs — line totals per commit — is what
/// analysis wrote there. This one needs the source text itself, which is not stored anywhere: the
/// analyser reads blobs, counts lines and keeps only the counts. Re-deriving the text from stored
/// figures is impossible, so the report is computed on demand from the local clone.</para>
///
/// <para><b>And why only the newest commit.</b> Health is a statement about the code as it stands.
/// Measuring every commit would multiply the work by the length of the history to produce a trend
/// nobody asked for, and the trend that IS wanted — is the repository growing — is already on the
/// charts.</para>
///
/// <para>Returns null when the repository is unknown or has never been analysed, which the
/// endpoint turns into a 404: a health report for a repository with no commits on record would be
/// a page of zeroes indistinguishable from genuinely terrible code.</para>
/// </summary>
public sealed class GetCodeHealthQueryHandler(
    IRepositoryDataService repositoryDataService,
    IGitHubService gitHubService,
    IUserPreferencesService userPreferencesService,
    ICodeHealthSnapshotStore snapshotStore,
    ILogger<GetCodeHealthQueryHandler> logger)
{
    public async Task<CodeHealthDto?> Handle(GetCodeHealthQuery request, CancellationToken cancellationToken = default)
    {
        var repository = await repositoryDataService.GetRepositoryByIdAsync(request.RepositoryId);
        if (repository is null) return null;

        var commits = (await repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId)).ToList();
        if (commits.Count == 0)
        {
            logger.LogInformation("Code health for {RepositoryId}: no analysed commits", request.RepositoryId);
            return null;
        }

        // The newest commit, on the same definition RepositoryTotals uses for "now" — never a
        // window, never a sum.
        var newest = commits.MaxBy(c => c.CommitDate)!;

        // The memo first. Every figure in the report is a pure function of the commit's tree, so a
        // SHA already scored can be answered from storage without opening the repository at all —
        // which is also the only way this works on a host whose clone has been recycled away.
        var memo = await snapshotStore.GetByRepositoryAsync(request.RepositoryId);
        if (memo.TryGetValue(newest.CommitSha, out var cached)
            && CodeHealthSnapshotSerializer.TryRead(cached.ReportJson, out var cachedReport))
        {
            logger.LogInformation("Code health for {RepositoryId} served from the memo at {Sha}",
                request.RepositoryId, cached.CommitSha);
            return cachedReport;
        }

        var extensions = repository.UserId != UserId.Empty
            ? await userPreferencesService.GetFileExtensionsAsync(repository.UserId)
            : UserPreferences.DefaultFileExtensions;

        var repositoryPath = gitHubService.ResolveRepositoryPath(repository.LocalPath);

        // No clone on disk (a recycled host, a deleted directory): the walk below yields no files
        // and the report correctly says "nothing to measure" (HasData false, no grade). What it
        // must not do is get memoised: saved under the newest SHA, that empty report was then
        // served from the memo forever, even after the clone came back.
        var hasClone = await gitHubService.IsRepositoryValidAsync(repositoryPath);

        // One Task.Run around the whole walk, not one per file. The enumeration is synchronous
        // git object reads plus regex matching — CPU-bound from end to end — and it holds the git
        // repository open for its duration, so it has to run as a single unit.
        var report = await Task.Run(
            () => CodeHealthReportFactory.Build(
                gitHubService.EnumerateSourceFiles(repositoryPath, newest.CommitSha, extensions)),
            cancellationToken);

        report.RepositoryId = repository.Id;
        report.Owner = repository.Owner;
        report.Name = repository.Name;
        report.CommitSha = GitClient.ShortSha(newest.CommitSha);
        report.CommitDate = newest.CommitDate;

        if (hasClone)
            await snapshotStore.SaveAsync(CodeHealthSnapshotSerializer.ToEntity(request.RepositoryId, newest.CommitSha, report));

        logger.LogInformation(
            "Code health for {Owner}/{Name}: {Score} ({Grade}) over {Files} files, "
            + "C# maintainability {Mi} over {Members} members",
            repository.Owner, repository.Name, report.Score, report.Grade, report.FilesAnalyzed,
            report.Metrics?.MaintainabilityIndex, report.Metrics?.MembersMeasured ?? 0);

        return report;
    }
}
