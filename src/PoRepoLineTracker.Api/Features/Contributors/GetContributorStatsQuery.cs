using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.Api.Features.Contributors;

public record GetContributorStatsQuery(RepositoryId RepositoryId, int Days = 365, int TopN = 10);

public class GetContributorStatsQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;
    private readonly ILogger<GetContributorStatsQueryHandler> _logger;

    public GetContributorStatsQueryHandler(
        IRepositoryDataService repositoryDataService,
        ILogger<GetContributorStatsQueryHandler> logger)
    {
        _repositoryDataService = repositoryDataService;
        _logger = logger;
    }

    public async Task<IEnumerable<ContributorStatsDto>> Handle(GetContributorStatsQuery request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting contributor stats for repository {RepositoryId} for last {Days} days",
            request.RepositoryId, request.Days);

        var commits = await _repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId);

        var cutoffDate = DateTime.UtcNow.AddDays(-request.Days);
        var filteredCommits = commits
            .Where(c => c.CommitDate >= cutoffDate)
            .OrderBy(c => c.CommitDate)
            .ToList();

        _logger.LogInformation("Found {Count} commits in date range for contributor analysis", filteredCommits.Count);

        // Get total lines for percentage calculation
        var totalLines = filteredCommits.Sum(c => c.LinesAdded);

        // Group by author
        var groupedByAuthor = filteredCommits
            .GroupBy(c => new { AuthorName = c.AuthorDisplayName, AuthorEmail = c.AuthorEmail })
            .Select(g =>
            {
                var authorTotalLines = g.Sum(c => c.LinesAdded);
                var authorTotalRemoved = g.Sum(c => c.LinesRemoved);

                // Daily breakdown for chart
                var dailyHistory = g
                    .GroupBy(c => c.CommitDate.Date)
                    .Select(dg => new DailyContributorStatsDto
                    {
                        Date = dg.Key,
                        LinesAdded = dg.Sum(c => c.LinesAdded),
                        Commits = dg.Count()
                    })
                    .OrderBy(d => d.Date)
                    .ToList();

                return new ContributorStatsDto
                {
                    AuthorName = g.Key.AuthorName,
                    AuthorEmail = g.Key.AuthorEmail,
                    TotalCommits = g.Count(),
                    TotalLinesAdded = authorTotalLines,
                    TotalLinesRemoved = authorTotalRemoved,
                    PercentageOfTotalLines = totalLines > 0 ? Math.Round((double)authorTotalLines / totalLines * 100, 2) : 0,
                    DailyHistory = dailyHistory
                };
            })
            .OrderByDescending(r => r.TotalLinesAdded)
            .Take(request.TopN)
            .ToList();

        _logger.LogInformation("Found {Count} contributors", groupedByAuthor.Count);
        return groupedByAuthor;
    }
}
