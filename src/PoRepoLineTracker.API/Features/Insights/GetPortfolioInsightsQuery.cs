using Microsoft.Extensions.Logging;

namespace PoRepoLineTracker.API.Features.Insights;

/// <summary>
/// Aggregates every tracked repository into the single figure set the All Time Insights dashboard
/// renders. Everything that page shows reads the whole history; the 30-day figures and the movers
/// are for the Repositories grid, which asks the same endpoint.
/// </summary>
public record GetPortfolioInsightsQuery(UserId UserId);

public sealed class GetPortfolioInsightsQueryHandler(
    IRepositoryDataService repositoryDataService,
    ILogger<GetPortfolioInsightsQueryHandler> logger)
{
    /// <summary>The least the activity heatmap shows: a younger portfolio still gets a year of cells.</summary>
    private const int ActivityWindowDays = 365;

    /// <summary>Window for the Repositories grid's "recently" figures — movers, commit count.</summary>
    private const int RecentWindowDays = 30;

    /// <summary>Extensions shown individually in the language mix; the rest fold into "Other".</summary>
    private const int LanguageMixSize = 8;

    /// <summary>
    /// Most points on the lifetime growth line: one a month for a history up to three years, then
    /// spread evenly. Each point is a snapshot lookup per repository, so the cap bounds the cost.
    /// </summary>
    private const int MaxTrendPoints = 36;

    /// <summary>The largest outliers ever, across every repository.</summary>
    private const int OutlierCount = 10;

    /// <summary>Weeks of commit cadence kept per repository for the ranking table's sparkline.</summary>
    private const int SparklineWeeks = 12;

    /// <summary>Extensions shown on the language-drift card, ranked by how far their share moved.</summary>
    private const int DriftCount = 6;

    /// <summary>
    /// Below this many lines at BOTH endpoints, an extension is left out of the drift ranking.
    /// A file type that went from 3 lines to 9 has tripled and tops the ranking on any relative
    /// measure, while saying nothing about how the time was spent.
    /// </summary>
    private const int DriftMinimumLines = 100;

    public async Task<PortfolioInsightsDto> Handle(GetPortfolioInsightsQuery request, CancellationToken cancellationToken = default)
    {
        var repositories = (await repositoryDataService.GetAllRepositoriesAsync(request.UserId)).ToList();

        var insights = new PortfolioInsightsDto
        {
            RepositoryCount = repositories.Count,
            AnalyzedCount = repositories.Count(r => r.LastAnalyzedCommitDate.HasValue)
        };

        if (repositories.Count == 0)
        {
            logger.LogInformation("Portfolio insights for user {UserId}: no repositories tracked", request.UserId);
            return insights;
        }

        // Fetched in parallel by the data service: one Azure Table round-trip per repository,
        // serialised, is what makes this page slow. Commits arrive ordered by date ascending.
        var commitsPerRepo = await repositoryDataService.GetAllRepositoriesWithCommitsAsync(request.UserId);

        var today = DateTime.UtcNow.Date;
        var recentCutoff = today.AddDays(-RecentWindowDays);
        var activityCutoff = today.AddDays(-(ActivityWindowDays - 1));

        var analysed = commitsPerRepo.Where(p => p.Commits.Count > 0).ToList();
        var firstCommitDay = analysed.Count > 0 ? analysed.Min(p => p.Commits[0].CommitDate.Date) : today;
        if (analysed.Count > 0) insights.FirstCommitDate = firstCommitDay;

        // Oldest first, so the trend chart and every sparkline read left-to-right as time passing.
        var trendDates = TrendDates(firstCommitDay, today);
        var trendTotals = new long[trendDates.Count];

        var movers = new List<RepositoryMovementDto>();
        var outliers = new List<CommitOutlierDto>();
        var languageTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // The drift baseline: each repository as its first analysed commit found it. Summed, that is
        // the portfolio as it started, so the card reads "since the beginning" like the rest of the
        // page; a repository added years after another still contributes its own starting mix.
        var languageBaseline = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var commitsByDay = new Dictionary<DateTime, (int Commits, int LinesAdded)>();

        long recentLinesAdded = 0, linesAdded = 0, linesRemoved = 0;
        int recentCommits = 0;

        foreach (var (repo, commits) in commitsPerRepo)
        {
            if (commits.Count == 0) continue;

            // Via RepositoryTotals so this page and the Repositories grid cannot drift: TotalLines
            // is a whole-repository snapshot taken at each commit, so "now" is the newest commit's
            // value and "30 days ago" is the newest at or before that date — never a sum, which
            // would count the same lines once per commit. `commits` is ordered, so commits[^1] is
            // the same value; the call is what documents and pins the definition.
            var latest = commits[^1];
            var latestTotal = RepositoryTotals.LatestTotalLines(commits);
            var baseline30 = RepositoryTotals.TotalLinesAsOf(commits, recentCutoff);

            insights.TotalLines += latestTotal;
            insights.CommitsAllTime += commits.Count;
            linesAdded += commits.Sum(c => (long)c.LinesAdded);
            linesRemoved += commits.Sum(c => (long)c.LinesRemoved);

            for (var i = 0; i < trendDates.Count; i++)
                trendTotals[i] += RepositoryTotals.TotalLinesAsOf(commits, trendDates[i]);

            foreach (var (extension, lines) in latest.LinesByFileType)
                languageTotals[extension] = languageTotals.GetValueOrDefault(extension) + lines;

            // The oldest commit's breakdown, a whole-repository snapshot like the newest's.
            foreach (var (extension, lines) in commits[0].LinesByFileType)
                languageBaseline[extension] = languageBaseline.GetValueOrDefault(extension) + lines;

            outliers.AddRange(CommitOutliers.Find(repo, commits, DateTime.MinValue));

            var recent = commits.Where(c => c.CommitDate >= recentCutoff).ToList();
            recentCommits += recent.Count;
            recentLinesAdded += recent.Sum(c => (long)c.LinesAdded);

            movers.Add(new RepositoryMovementDto
            {
                RepositoryId = repo.Id,
                Owner = repo.Owner,
                Name = repo.Name,
                TotalLines = latestTotal,
                NetChange30Days = latestTotal - baseline30,
                Commits30Days = recent.Count,
                WeeklyCommits = WeeklyCadence(commits, today)
            });

            // Every commit: the heatmap and the streaks read the whole history.
            foreach (var commit in commits)
            {
                var day = commit.CommitDate.Date;
                var existing = commitsByDay.GetValueOrDefault(day);
                commitsByDay[day] = (existing.Commits + 1, existing.LinesAdded + commit.LinesAdded);
            }
        }

        insights.Commits30Days = recentCommits;
        insights.LinesAdded30Days = (int)Math.Min(recentLinesAdded, int.MaxValue);
        insights.LinesAddedAllTime = (int)Math.Min(linesAdded, int.MaxValue);
        insights.LinesRemovedAllTime = (int)Math.Min(linesRemoved, int.MaxValue);
        insights.Outliers = outliers.OrderByDescending(o => o.LinesAdded).Take(OutlierCount).ToList();

        insights.Movers = movers.OrderByDescending(m => m.NetChange30Days).ToList();
        insights.LanguageMix = BuildLanguageMix(languageTotals);
        insights.LanguageDrift = BuildDrift(languageBaseline, languageTotals);
        insights.RisingLanguage = insights.LanguageDrift.FirstOrDefault(d => d.PercentDelta > 0)?.Extension;
        insights.FadingLanguage = insights.LanguageDrift.LastOrDefault(d => d.PercentDelta < 0)?.Extension;
        // From the first commit, or a year back when the history is shorter than that — the
        // year is the least the heatmap has always shown. ponytail: capped at ten years so one
        // commit with a bogus 1970 author date cannot produce twenty thousand empty cells; raise
        // the cap if a portfolio really is older.
        var activityStart = new[] { activityCutoff, firstCommitDay }.Min();
        if (activityStart < today.AddYears(-10)) activityStart = today.AddYears(-10);
        insights.Activity = BuildActivity(commitsByDay, activityStart, today);
        insights.TrendLine = trendDates
            .Select((date, i) => new PortfolioTrendPointDto { Date = date, TotalLines = (int)Math.Min(trendTotals[i], int.MaxValue) })
            .ToList();

        var activeDates = commitsByDay.Keys.ToHashSet();
        insights.ActiveDaysAllTime = activeDates.Count;
        insights.ActiveDays30 = activeDates.Count(d => d >= recentCutoff);
        insights.CurrentStreakDays = CommitStreaks.Current(activeDates, today);
        insights.LongestStreakDays = CommitStreaks.Longest(activeDates);

        logger.LogInformation(
            "Portfolio insights for user {UserId}: {Repos} repos, {Lines} lines, {Commits} commits in {Days}d",
            request.UserId, insights.RepositoryCount, insights.TotalLines, insights.Commits30Days, RecentWindowDays);

        return insights;
    }

    /// <summary>
    /// Sample dates for the lifetime growth line, oldest first and ending today: one a month from
    /// the first commit, or <see cref="MaxTrendPoints"/> spread evenly over a longer history. A
    /// history shorter than a day is the single point today.
    /// </summary>
    internal static List<DateTime> TrendDates(DateTime first, DateTime today)
    {
        var span = (today - first).TotalDays;
        if (span < 1) return [today];

        var months = (today.Year - first.Year) * 12 + today.Month - first.Month;
        var points = Math.Clamp(months + 1, 2, MaxTrendPoints);
        return Enumerable.Range(0, points)
            .Select(i => first.AddDays(Math.Round(span * i / (points - 1))))
            .ToList();
    }

    /// <summary>
    /// Commits per week for the last <see cref="SparklineWeeks"/> weeks, oldest first. Whole
    /// 7-day buckets ending today, not calendar weeks — the sparkline compares repositories against
    /// each other, not against a shared calendar boundary that would rarely land on "today".
    /// <paramref name="commits"/> need not be filtered or ordered; every commit is checked once.
    /// </summary>
    private static List<int> WeeklyCadence(IReadOnlyList<CommitLineCount> commits, DateTime today)
    {
        var weeks = new int[SparklineWeeks];
        foreach (var commit in commits)
        {
            var daysAgo = (today - commit.CommitDate.Date).Days;
            if (daysAgo < 0 || daysAgo >= 7 * SparklineWeeks) continue;

            weeks[SparklineWeeks - 1 - daysAgo / 7]++;
        }
        return weeks.ToList();
    }

    private static List<LanguageShareDto> BuildLanguageMix(Dictionary<string, long> totals)
    {
        var grandTotal = totals.Values.Sum();
        if (grandTotal <= 0) return [];

        var ranked = totals.OrderByDescending(kv => kv.Value).ToList();
        var mix = ranked.Take(LanguageMixSize)
            .Select(kv => new LanguageShareDto
            {
                Extension = kv.Key,
                Lines = (int)Math.Min(kv.Value, int.MaxValue),
                Percentage = Math.Round((double)kv.Value / grandTotal * 100, 1)
            })
            .ToList();

        var remainder = ranked.Skip(LanguageMixSize).Sum(kv => kv.Value);
        if (remainder > 0)
        {
            mix.Add(new LanguageShareDto
            {
                Extension = "Other",
                Lines = (int)Math.Min(remainder, int.MaxValue),
                Percentage = Math.Round((double)remainder / grandTotal * 100, 1)
            });
        }

        return mix;
    }

    /// <summary>
    /// Ranks extensions by how far their SHARE of the portfolio moved across the window.
    ///
    /// <para>Share rather than absolute lines, because absolute lines answer the question the
    /// language-mix card beside it already answers. A share that grew while the language itself
    /// shrank means everything else shrank faster — which is the drift worth showing, and is
    /// invisible in a line-count delta.</para>
    ///
    /// <para>Ordered by signed delta, largest gain first, so the caller can read the rising language
    /// off the front and the fading one off the back.</para>
    /// </summary>
    private static List<LanguageDriftDto> BuildDrift(Dictionary<string, long> start, Dictionary<string, long> end)
    {
        var startTotal = start.Values.Sum();
        var endTotal = end.Values.Sum();

        // A portfolio that did not exist at the start of the window has no share to have moved from.
        if (startTotal <= 0 || endTotal <= 0) return [];

        var drift = new List<LanguageDriftDto>();

        foreach (var extension in start.Keys.Union(end.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var startLines = start.GetValueOrDefault(extension);
            var endLines = end.GetValueOrDefault(extension);
            if (startLines < DriftMinimumLines && endLines < DriftMinimumLines) continue;

            var startPercent = Math.Round((double)startLines / startTotal * 100, 1);
            var endPercent = Math.Round((double)endLines / endTotal * 100, 1);

            drift.Add(new LanguageDriftDto
            {
                Extension = extension,
                StartLines = (int)Math.Min(startLines, int.MaxValue),
                EndLines = (int)Math.Min(endLines, int.MaxValue),
                StartPercent = startPercent,
                EndPercent = endPercent,
                PercentDelta = Math.Round(endPercent - startPercent, 1)
            });
        }

        // Take the largest movers in EITHER direction, then re-sort signed so the list reads
        // gainers-first. Sorting signed before taking would drop every fading language.
        //
        // Rows that did not move are dropped rather than ranked last: a language holding exactly its
        // share is not drift, and including it produced a card whose every row read "70% → 70%,
        // +0.0 pts" with an empty sentence above it, because there was no rising or fading language
        // for the sentence to name. Filtering here means the card simply does not render.
        return drift
            .Where(d => d.PercentDelta != 0)
            .OrderByDescending(d => Math.Abs(d.PercentDelta))
            .Take(DriftCount)
            .OrderByDescending(d => d.PercentDelta)
            .ToList();
    }

    /// <summary>
    /// One entry per calendar day across the window, zero-commit days included. The heatmap draws a
    /// fixed grid, so a sparse list would silently shift every cell after the first gap.
    /// </summary>
    private static List<ActivityDayDto> BuildActivity(
        Dictionary<DateTime, (int Commits, int LinesAdded)> byDay, DateTime from, DateTime to)
    {
        var days = new List<ActivityDayDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var entry = byDay.GetValueOrDefault(date);
            days.Add(new ActivityDayDto { Date = date, Commits = entry.Commits, LinesAdded = entry.LinesAdded });
        }
        return days;
    }
}

/// <summary>
/// Flags commits that are far larger than their repository's own norm.
///
/// <para>Statistics, not a model: a commit is an outlier when its lines-added sits more than
/// three standard deviations above the repository's mean on a log scale (commit sizes are
/// roughly log-normal — on a linear scale one big commit drags the mean up and hides itself).
/// Two floors keep it quiet: a repository needs enough history for "normal" to mean anything,
/// and a commit must be large in absolute terms, so a 40-line commit in a repository of 3-line
/// commits is not reported.</para>
/// </summary>
internal static class CommitOutliers
{
    private const int MinimumHistory = 20;
    private const int MinimumLinesAdded = 1_000;
    private const double Sigmas = 3;

    internal static IEnumerable<CommitOutlierDto> Find(
        GitHubRepository repository, IReadOnlyList<CommitLineCount> commits, DateTime since)
    {
        if (commits.Count < MinimumHistory) return [];

        var logs = commits.Select(c => Math.Log(1 + Math.Max(0, c.LinesAdded))).ToList();
        var mean = logs.Average();
        var deviation = Math.Sqrt(logs.Sum(l => (l - mean) * (l - mean)) / logs.Count);
        var threshold = mean + Sigmas * deviation;

        var sorted = commits.Select(c => c.LinesAdded).Order().ToList();
        var median = sorted[sorted.Count / 2];

        return commits
            .Where(c => c.CommitDate >= since
                        && c.LinesAdded >= MinimumLinesAdded
                        && Math.Log(1 + c.LinesAdded) > threshold)
            .OrderByDescending(c => c.LinesAdded)
            .Select(c => new CommitOutlierDto
            {
                RepositoryId = repository.Id,
                Owner = repository.Owner,
                Name = repository.Name,
                CommitSha = GitClient.ShortSha(c.CommitSha),
                CommitDate = c.CommitDate,
                LinesAdded = c.LinesAdded,
                TypicalLinesAdded = median
            });
    }
}
