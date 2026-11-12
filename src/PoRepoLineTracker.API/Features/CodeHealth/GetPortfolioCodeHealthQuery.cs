using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.Api.Features.CodeHealth;

/// <summary>Every repository the caller owns, scored so they can be ranked against each other.</summary>
public record GetPortfolioCodeHealthQuery(UserId UserId);

/// <summary>
/// <para><b>Why this is worth a page when the per-repository report already exists.</b> The
/// absolute score is a guide; the RANKING is the part that holds up. One repository's 71 says
/// little on its own, but "this one is the worst of your nine" is directly actionable, and the
/// per-repository card cannot show it.</para>
///
/// <para><b>Cost, and how it is kept bounded.</b> Scoring a repository means walking its whole tree
/// and parsing every C# file — seconds each — so doing that for every repository on every page load
/// would be the most expensive request in the app. Two things keep it in hand. Repositories with no
/// analysed commits, and those whose clone is not on this host, are settled by a storage lookup and
/// a <c>Directory.Exists</c>: they cost nothing and are returned as unmeasured rows. And the
/// remainder are walked once each, sequentially — the work is CPU-bound, so running it in parallel
/// on the shared web host would trade a slow page for a stalled one.</para>
///
/// <para>Ordering is highest health first, so the list reads as a leaderboard. Unmeasured rows
/// sort last regardless of direction: they are information, not a verdict, and letting a repository
/// with no score sit among the graded ones would imply a ranking it never earned.</para>
/// </summary>
public sealed class GetPortfolioCodeHealthQueryHandler(
    IRepositoryDataService repositoryDataService,
    GetCodeHealthQueryHandler codeHealthQueryHandler,
    IGitHubService gitHubService,
    ILogger<GetPortfolioCodeHealthQueryHandler> logger)
{
    public async Task<List<CodeHealthSummaryDto>> Handle(
        GetPortfolioCodeHealthQuery request, CancellationToken cancellationToken = default)
    {
        var repositories = (await repositoryDataService.GetAllRepositoriesAsync(request.UserId)).ToList();
        var summaries = new List<CodeHealthSummaryDto>(repositories.Count);

        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = new CodeHealthSummaryDto
            {
                RepositoryId = repository.Id,
                Owner = repository.Owner,
                Name = repository.Name
            };

            // The cheap disqualifier first: no clone on this host means nothing can be read, and
            // saying so costs a directory probe rather than a tree walk.
            var path = gitHubService.ResolveRepositoryPath(repository.LocalPath);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                summary.UnmeasuredReason = "Not cloned on this server — run an analysis to measure it.";
                summaries.Add(summary);
                continue;
            }

            try
            {
                var report = await codeHealthQueryHandler.Handle(new GetCodeHealthQuery(repository.Id), cancellationToken);

                if (report is null)
                {
                    summary.UnmeasuredReason = "No analysed commits yet.";
                }
                else if (!report.HasData)
                {
                    summary.UnmeasuredReason = "No counted source files at the newest commit.";
                }
                else
                {
                    summary.Measured = true;
                    summary.Score = report.OverallScore;
                    summary.Grade = report.OverallGrade;
                    summary.CommitSha = report.CommitSha;
                    summary.CommitDate = report.CommitDate;
                    summary.MaintainabilityIndex = report.Metrics?.MaintainabilityIndex;
                    summary.CyclomaticComplexity = report.Metrics?.CyclomaticComplexity ?? report.TotalComplexity;
                    summary.LinesOfSourceCode = report.Metrics?.LinesOfSourceCode ?? report.CodeLines;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable repository must not lose the other eight. A corrupt clone or a
                // locked object store is a fact about that row, not a failure of the page.
                logger.LogWarning(ex, "Code health failed for {Owner}/{Name}; reporting it as unmeasured",
                    repository.Owner, repository.Name);
                summary.UnmeasuredReason = "Could not be read on this server.";
            }

            summaries.Add(summary);
        }

        // Best first among the measured; unmeasured last. Ranked on the single combined score,
        // which is the whole reason it exists — ordering on the C# index alone put a repository
        // reading "62, grade A" above one reading "67, grade F".
        return summaries
            .OrderByDescending(s => s.Measured)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
