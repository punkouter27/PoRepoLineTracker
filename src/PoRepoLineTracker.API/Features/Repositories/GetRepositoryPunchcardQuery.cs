using Microsoft.Extensions.Logging;
using PoRepoLineTracker.Api.Storage;
using PoRepoLineTracker.Shared.Models.Dtos;

namespace PoRepoLineTracker.Api.Features.Repositories;

public record GetRepositoryPunchcardQuery(RepositoryId RepositoryId, int Days = 365);

public class GetRepositoryPunchcardQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;
    private readonly ILogger<GetRepositoryPunchcardQueryHandler> _logger;

    public GetRepositoryPunchcardQueryHandler(
        IRepositoryDataService repositoryDataService,
        ILogger<GetRepositoryPunchcardQueryHandler> logger)
    {
        _repositoryDataService = repositoryDataService;
        _logger = logger;
    }

    public async Task<List<PunchcardItemDto>> Handle(GetRepositoryPunchcardQuery request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting commit punchcard for repository {RepositoryId} for last {Days} days",
            request.RepositoryId, request.Days);

        var commits = await _repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId);

        var cutoffDate = DateTime.UtcNow.AddDays(-request.Days);
        var filteredCommits = commits
            .Where(c => c.CommitDate >= cutoffDate)
            .ToList();

        return AggregatePunchcard(filteredCommits);
    }

    public static List<PunchcardItemDto> AggregatePunchcard(IEnumerable<CommitLineCount> commits)
    {
        // 7 days x 24 hours grid
        var grid = new Dictionary<(int Day, int Hour), (int Commits, int Added, int Removed)>();

        foreach (var c in commits)
        {
            var day = (int)c.CommitDate.DayOfWeek;
            var hour = c.CommitDate.Hour;
            var key = (day, hour);

            if (grid.TryGetValue(key, out var current))
            {
                grid[key] = (current.Commits + 1, current.Added + c.LinesAdded, current.Removed + c.LinesRemoved);
            }
            else
            {
                grid[key] = (1, c.LinesAdded, c.LinesRemoved);
            }
        }

        var results = new List<PunchcardItemDto>();
        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                if (grid.TryGetValue((day, hour), out var data))
                {
                    results.Add(new PunchcardItemDto
                    {
                        DayOfWeek = day,
                        HourOfDay = hour,
                        CommitCount = data.Commits,
                        LinesAdded = data.Added,
                        LinesRemoved = data.Removed
                    });
                }
                else
                {
                    results.Add(new PunchcardItemDto
                    {
                        DayOfWeek = day,
                        HourOfDay = hour,
                        CommitCount = 0,
                        LinesAdded = 0,
                        LinesRemoved = 0
                    });
                }
            }
        }

        return results;
    }
}

