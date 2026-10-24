namespace PoRepoLineTracker.API.Features.Insights;

/// <param name="UtcOffsetMinutes">The viewer's offset from UTC (local minus UTC), so days and
/// hours are theirs. Clamped to the real range of ±14 hours.</param>
public record GetYearInReviewQuery(UserId UserId, int Year, int UtcOffsetMinutes);

/// <summary>
/// The Year in Review figures. In the Insights slice beside the digest because it reads the same
/// fan-out (<see cref="IRepositoryDataService.GetAllRepositoriesWithCommitsAsync"/>) and uses the
/// same shared definitions of a streak and a size; it is a separate handler because a year needs
/// almost none of the digest's output.
///
/// <para>ponytail: one fixed offset for the whole year, so commits on the far side of a DST change
/// are an hour off in the busiest-hour figure. Accurate per-commit zones would need the viewer's
/// IANA zone and a tz database in the request path, for one chart.</para>
/// </summary>
public sealed class GetYearInReviewQueryHandler(IRepositoryDataService repositoryDataService)
{
    public async Task<YearInReviewDto> Handle(GetYearInReviewQuery request, CancellationToken cancellationToken = default)
    {
        var offset = TimeSpan.FromMinutes(Math.Clamp(request.UtcOffsetMinutes, -14 * 60, 14 * 60));
        var data = await repositoryDataService.GetAllRepositoriesWithCommitsAsync(request.UserId);
        return Build(data, request.Year, offset);
    }

    internal static YearInReviewDto Build(
        IReadOnlyList<(GitHubRepository Repository, IReadOnlyList<CommitLineCount> Commits)> data,
        int year,
        TimeSpan offset)
    {
        var result = new YearInReviewDto
        {
            Year = year,
            MonthlyCommits = [.. new int[12]],
            AvailableYears = data.SelectMany(d => d.Commits)
                .Select(c => (c.CommitDate + offset).Year)
                .Distinct()
                .OrderDescending()
                .ToList()
        };

        // The local year's bounds, in UTC, for the size snapshots either side of it.
        var startUtc = DateTime.SpecifyKind(new DateTime(year, 1, 1), DateTimeKind.Utc) - offset;
        var endUtc = DateTime.SpecifyKind(new DateTime(year + 1, 1, 1), DateTimeKind.Utc) - offset;

        var activeDates = new HashSet<DateTime>();
        var linesAddedByDay = new Dictionary<DateTime, int>();
        var hours = new int[24];
        var weekdays = new int[7];
        var startMix = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var endMix = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (repository, commits) in data)
        {
            result.PortfolioLinesStart += RepositoryTotals.TotalLinesAsOf(commits, startUtc.AddTicks(-1));
            result.PortfolioLinesEnd += RepositoryTotals.TotalLinesAsOf(commits, endUtc.AddTicks(-1));
            foreach (var (extension, lines) in RepositoryTotals.LinesByFileTypeAsOf(commits, startUtc.AddTicks(-1)))
                startMix[extension] = startMix.GetValueOrDefault(extension) + lines;
            foreach (var (extension, lines) in RepositoryTotals.LinesByFileTypeAsOf(commits, endUtc.AddTicks(-1)))
                endMix[extension] = endMix.GetValueOrDefault(extension) + lines;

            var repositoryCommits = 0;
            foreach (var commit in commits)
            {
                var local = commit.CommitDate + offset;
                if (local.Year != year) continue;

                repositoryCommits++;
                result.Commits++;
                result.LinesAdded += commit.LinesAdded;
                result.LinesRemoved += commit.LinesRemoved;
                result.MonthlyCommits[local.Month - 1]++;
                hours[local.Hour]++;
                weekdays[(int)local.DayOfWeek]++;
                activeDates.Add(local.Date);
                linesAddedByDay[local.Date] = linesAddedByDay.GetValueOrDefault(local.Date) + commit.LinesAdded;
            }

            if (repositoryCommits == 0) continue;
            result.RepositoriesTouched++;
            if (repositoryCommits > result.TopRepositoryCommits)
            {
                result.TopRepository = repository.Name;
                result.TopRepositoryCommits = repositoryCommits;
            }
        }

        if (result.Commits == 0) return result;

        result.HasActivity = true;
        result.ActiveDays = activeDates.Count;
        result.LongestStreakDays = CommitStreaks.Longest(activeDates);
        result.BusiestMonth = IndexOfMax(result.MonthlyCommits) + 1;
        result.PeakHour = IndexOfMax(hours);
        result.PeakWeekday = IndexOfMax(weekdays);

        var (biggestDay, biggestLines) = linesAddedByDay.MaxBy(kv => kv.Value);
        result.BiggestDay = biggestDay;
        result.BiggestDayLinesAdded = biggestLines;

        // Share, not lines (SPEC §10): a type can gain share while shrinking, when the rest shrank faster.
        var startTotal = startMix.Values.Sum();
        var endTotal = endMix.Values.Sum();
        if (endTotal > 0)
        {
            var best = endMix.Keys.Union(startMix.Keys, StringComparer.OrdinalIgnoreCase)
                .Select(extension => (Extension: extension, Points:
                    100.0 * endMix.GetValueOrDefault(extension) / endTotal
                    - (startTotal > 0 ? 100.0 * startMix.GetValueOrDefault(extension) / startTotal : 0)))
                .OrderByDescending(x => x.Points)
                .ThenBy(x => x.Extension, StringComparer.Ordinal)
                .First();
            if (best.Points > 0)
            {
                result.TopGrowingExtension = best.Extension;
                result.TopGrowingSharePoints = Math.Round(best.Points, 1);
            }
        }

        return result;
    }

    /// <summary>First index of the largest value, so ties go to the earlier month/hour/day.</summary>
    private static int IndexOfMax(IReadOnlyList<int> values)
    {
        var best = 0;
        for (var i = 1; i < values.Count; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }
}
