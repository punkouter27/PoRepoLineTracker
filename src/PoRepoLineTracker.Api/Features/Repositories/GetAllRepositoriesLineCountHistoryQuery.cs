using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace PoRepoLineTracker.Api.Features.Repositories;

public record GetAllRepositoriesLineCountHistoryQuery(int Days, UserId UserId);

public class GetAllRepositoriesLineCountHistoryQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;

    public GetAllRepositoriesLineCountHistoryQueryHandler(IRepositoryDataService repositoryDataService)
    {
        _repositoryDataService = repositoryDataService;
    }

    public async Task<IEnumerable<RepositoryLineCountHistoryDto>> Handle(GetAllRepositoriesLineCountHistoryQuery request, CancellationToken cancellationToken = default)
    {
        var allRepositories = (await _repositoryDataService.GetAllRepositoriesAsync(request.UserId)).ToList();

        // Fetch all repos' commit data in parallel — eliminates N sequential Azure Table round-trips
        var fetchTasks = allRepositories.Select(repo =>
            _repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(repo.Id)
                .ContinueWith(t => (repo, commits: t.Result), TaskContinuationOptions.OnlyOnRanToCompletion));

        var results = await Task.WhenAll(fetchTasks);

        return results.Select(r =>
        {
            var dailyLineCounts = r.commits
                .Where(clc => clc.CommitDate >= DateTimeOffset.UtcNow.AddDays(-request.Days))
                .GroupBy(clc => clc.CommitDate.Date)
                .Select(g => new DailyLineCountDto
                {
                    Date = g.Key,
                    // TotalLines is a per-commit snapshot; use the last commit of the day
                    TotalLines = g.OrderByDescending(clc => clc.CommitDate).First().TotalLines,
                    TotalLinesAdded = g.Sum(clc => clc.LinesAdded),
                    TotalLinesDeleted = g.Sum(clc => clc.LinesRemoved),
                    TotalLinesChanged = g.Sum(clc => clc.LinesAdded + clc.LinesRemoved)
                })
                .OrderBy(dlc => dlc.Date)
                .ToList();

            return new RepositoryLineCountHistoryDto
            {
                RepositoryId = r.repo.Id,
                RepositoryName = r.repo.Name,
                Owner = r.repo.Owner,
                // From the UNFILTERED commit list, on purpose. The series above is windowed
                // because that is what a chart wants; the current size is not, because a
                // repository whose last commit predates the window still has its code. Deriving
                // this from the last point of the series is what made the Repositories page and
                // Insights print different numbers under the same label.
                TotalLines = RepositoryTotals.LatestTotalLines(r.commits),
                DailyLineCounts = dailyLineCounts
            };
        });
    }
}
