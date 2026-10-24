using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PoRepoLineTracker.API.Features.Insights;

namespace PoRepoLineTracker.Unit;

/// <summary>
/// The Insights dashboard is one query over every commit of every repository, and every figure it
/// prints is arithmetic with a way to be subtly wrong: a snapshot summed instead of taken, a
/// baseline read from the wrong end of a window, a streak that counts today as broken before the
/// day is over. None of it had a test.
///
/// <para>The handler is exercised through <c>Handle</c> rather than its private helpers, so these
/// assert the figures the page actually renders — one test per behaviour, with the assertions for
/// a behaviour grouped over a single arranged portfolio.</para>
/// </summary>
public class GetPortfolioInsightsQueryHandlerTests
{
    private readonly IRepositoryDataService _dataService = Substitute.For<IRepositoryDataService>();
    private readonly GetPortfolioInsightsQueryHandler _sut;
    private readonly UserId _userId = UserId.New();

    /// <summary>Anchored to UTC midnight because the handler's windows are all day-aligned.</summary>
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    public GetPortfolioInsightsQueryHandlerTests()
    {
        _sut = new GetPortfolioInsightsQueryHandler(
            _dataService, Substitute.For<ILogger<GetPortfolioInsightsQueryHandler>>());
    }

    private GitHubRepository GivenRepository(string owner, string name, params CommitLineCount[] commits)
    {
        var repo = new GitHubRepository
        {
            Id = RepositoryId.New(),
            UserId = _userId,
            Owner = owner,
            Name = name,
            LastAnalyzedCommitDate = commits.Length > 0 ? commits.Max(c => c.CommitDate) : null
        };

        foreach (var commit in commits) commit.RepositoryId = repo.Id;
        _dataService.GetCommitLineCountsByRepositoryIdAsync(repo.Id).Returns(commits.ToList());
        return repo;
    }

    /// <summary>Captured per-repo commits; lifted into the new GetAllRepositoriesWithCommitsAsync stub.</summary>
    private readonly List<GitHubRepository> _reposForLiftedStub = new();

    private void GivenRepositories(params GitHubRepository[] repositories)
    {
        _dataService.GetAllRepositoriesAsync(_userId).Returns(repositories.ToList());

        // The handler now reads commits through the lifted fan-out, which pairs each repository
        // with its commits already ordered by date ascending. Build that map from the per-repo
        // setups above and hand it to the substitute.
        _reposForLiftedStub.Clear();
        _reposForLiftedStub.AddRange(repositories);

        var pairs = repositories
            .Select(repo => (
                Repository: repo,
                Commits: (IReadOnlyList<CommitLineCount>)_dataService
                    .GetCommitLineCountsByRepositoryIdAsync(repo.Id)
                    .GetAwaiter()
                    .GetResult()
                    .OrderBy(c => c.CommitDate)
                    .ToList()))
            .ToList();

        _dataService.GetAllRepositoriesWithCommitsAsync(_userId).Returns(pairs);
    }

    private static CommitLineCount Commit(
        int daysAgo,
        int totalLines,
        int linesAdded = 0,
        int linesRemoved = 0,
        Dictionary<string, int>? byFileType = null) => new()
        {
            Id = Guid.NewGuid(),
            CommitSha = Guid.NewGuid().ToString("N")[..7],
            CommitDate = Today.AddDays(-daysAgo),
            TotalLines = totalLines,
            LinesAdded = linesAdded,
            LinesRemoved = linesRemoved,
            LinesByFileType = byFileType ?? []
        };

    private Task<PortfolioInsightsDto> WhenQueried() =>
        _sut.Handle(new GetPortfolioInsightsQuery(_userId), CancellationToken.None);

    // ─── Totals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Totals_HandlesEmptyRepositoriesAndSumsNewestSnapshots()
    {
        GivenRepositories();
        var empty = await WhenQueried();
        empty.RepositoryCount.Should().Be(0);
        empty.TotalLines.Should().Be(0);
        empty.Movers.Should().BeEmpty();
        empty.LanguageMix.Should().BeEmpty();
        empty.Activity.Should().BeEmpty();

        GivenRepositories(
            GivenRepository("acme", "api",
                Commit(daysAgo: 10, totalLines: 400),
                Commit(daysAgo: 5, totalLines: 700),
                Commit(daysAgo: 1, totalLines: 1_000)),
            GivenRepository("acme", "archived", Commit(daysAgo: 800, totalLines: 5_000)),
            GivenRepository("acme", "never-analyzed"));

        var insights = await WhenQueried();
        insights.TotalLines.Should().Be(6_000);
        insights.RepositoryCount.Should().Be(3);
        insights.AnalyzedCount.Should().Be(2);
    }

    // ─── Lifetime totals ─────────────────────────────────────────────────────

    /// <summary>
    /// The All Time Insights page reads the whole history: every commit and every line written or
    /// removed counts, however old, and the growth line starts at the first commit.
    /// </summary>
    [Fact]
    public async Task LifetimeTotals_CountEveryCommit_AndTheGrowthLineStartsAtTheFirst()
    {
        GivenRepositories(
            GivenRepository("acme", "api",
                Commit(daysAgo: 900, totalLines: 1_000, linesAdded: 1_000),
                Commit(daysAgo: 20, totalLines: 1_400, linesAdded: 500, linesRemoved: 100),
                Commit(daysAgo: 2, totalLines: 1_500, linesAdded: 150, linesRemoved: 50)),
            GivenRepository("acme", "web",
                Commit(daysAgo: 10, totalLines: 300, linesAdded: 300)));

        var insights = await WhenQueried();

        insights.CommitsAllTime.Should().Be(4);
        insights.LinesAddedAllTime.Should().Be(1_950);
        insights.LinesRemovedAllTime.Should().Be(150);
        insights.ActiveDaysAllTime.Should().Be(4);
        insights.FirstCommitDate.Should().Be(Today.AddDays(-900));
        insights.Commits30Days.Should().Be(3, "the Repositories grid's 30-day figure is unchanged");

        insights.TrendLine.First().Date.Should().Be(Today.AddDays(-900));
        insights.TrendLine.Last().Date.Should().Be(Today);
        insights.TrendLine.First().TotalLines.Should().Be(1_000);
        insights.TrendLine.Last().TotalLines.Should().Be(1_800, "snapshots, summed across repositories");
        insights.TrendLine.Should().HaveCountLessThanOrEqualTo(36);
    }

    [Fact]
    public void TrendDates_AreMonthlyUpToThreeYears_ThenCappedAndSpreadEvenly()
    {
        GetPortfolioInsightsQueryHandler.TrendDates(Today, Today).Should().Equal(Today);
        GetPortfolioInsightsQueryHandler.TrendDates(Today.AddMonths(-6), Today).Should().HaveCount(7);

        var decade = GetPortfolioInsightsQueryHandler.TrendDates(Today.AddYears(-10), Today);
        decade.Should().HaveCount(36).And.BeInAscendingOrder().And.OnlyHaveUniqueItems();
        decade[0].Should().Be(Today.AddYears(-10));
        decade[^1].Should().Be(Today);
    }

    // ─── Movers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Movers_RankedByNetChangeGainersFirst()
    {
        GivenRepositories(
            GivenRepository("acme", "shrinking",
                Commit(daysAgo: 40, totalLines: 1_000),
                Commit(daysAgo: 1, totalLines: 400)),
            GivenRepository("acme", "growing",
                Commit(daysAgo: 40, totalLines: 100),
                Commit(daysAgo: 1, totalLines: 900)),
            GivenRepository("acme", "empty"));

        var insights = await WhenQueried();
        insights.Movers.Should().HaveCount(2);
        insights.Movers[0].Name.Should().Be("growing");
        insights.Movers[0].NetChange30Days.Should().Be(800);
        insights.Movers[^1].Name.Should().Be("shrinking");
        insights.Movers[^1].NetChange30Days.Should().Be(-600);
    }

    // ─── Language mix ────────────────────────────────────────────────────────

    [Fact]
    public async Task LanguageMix_RanksNewestSnapshotsAndFoldsBeyondTopEight()
    {
        GivenRepositories(GivenRepository("acme", "api",
            Commit(daysAgo: 10, totalLines: 500, byFileType: new Dictionary<string, int> { [".js"] = 500 }),
            Commit(daysAgo: 1, totalLines: 1_000, byFileType: new Dictionary<string, int>
            {
                [".cs"] = 750,
                [".razor"] = 250
            })));

        var insights = await WhenQueried();
        insights.LanguageMix.Select(l => l.Extension).Should().ContainInOrder(".cs", ".razor");
        insights.LanguageMix.Should().NotContain(l => l.Extension == ".js");
        insights.LanguageMix.Sum(l => l.Percentage).Should().BeApproximately(100, 0.1);
        insights.LanguageMix[0].Lines.Should().Be(750);

        var byFileType = Enumerable.Range(1, 12)
            .ToDictionary(i => $".ext{i:00}", i => 100 - i);
        GivenRepositories(GivenRepository("acme", "api12",
            Commit(daysAgo: 1, totalLines: byFileType.Values.Sum(), byFileType: byFileType)));

        var topEight = await WhenQueried();
        topEight.LanguageMix.Should().HaveCount(9);
        topEight.LanguageMix[^1].Extension.Should().Be("Other");
        topEight.LanguageMix.Sum(l => l.Percentage).Should().BeApproximately(100, 0.2);
    }

    // ─── Language drift ──────────────────────────────────────────────────────

    /// <summary>
    /// Drift is a move in SHARE, not in lines — the whole reason the card exists beside the mix.
    /// This portfolio is built so the two disagree in the direction that matters: .js grows
    /// outright (400 → 500 lines) while its share collapses (100% → 25%), because .cs arrived.
    /// A line-count delta would rank .js as the year's gainer.
    /// </summary>
    [Fact]
    public async Task LanguageDrift_RanksBySharePointsNotLines_AndNamesTheRisingAndFadingTypes()
    {
        GivenRepositories(GivenRepository("acme", "api",
            // The baseline drift reads: the repository's first analysed snapshot.
            Commit(daysAgo: 400, totalLines: 400, byFileType: new Dictionary<string, int> { [".js"] = 400 }),
            Commit(daysAgo: 1, totalLines: 2_000, byFileType: new Dictionary<string, int>
            {
                [".cs"] = 1_500,
                [".js"] = 500
            })));

        var insights = await WhenQueried();

        var js = insights.LanguageDrift.Single(d => d.Extension == ".js");
        js.EndLines.Should().BeGreaterThan(js.StartLines, "the language itself grew");
        js.PercentDelta.Should().BeNegative("but everything else grew faster, so its share fell");

        // Gainers first, so the page reads the rising language off the front and the fading one
        // off the back.
        insights.LanguageDrift[0].Extension.Should().Be(".cs");
        insights.RisingLanguage.Should().Be(".cs");
        insights.FadingLanguage.Should().Be(".js");
    }

    /// <summary>
    /// Two ways there is no drift to report, both of which must yield an EMPTY card rather than a
    /// card full of zeroes. A repository with one snapshot has nothing to have moved from. And a
    /// portfolio that only grew uniformly has moved no shares at all.
    /// </summary>
    [Fact]
    public async Task LanguageDrift_IsEmpty_WithASingleSnapshot_OrWhenNothingMoved()
    {
        GivenRepositories(GivenRepository("acme", "new",
            Commit(daysAgo: 1, totalLines: 900, byFileType: new Dictionary<string, int> { [".cs"] = 600, [".js"] = 300 })));

        var young = await WhenQueried();
        young.LanguageDrift.Should().BeEmpty("its first snapshot is its newest");
        young.RisingLanguage.Should().BeNull();
        young.FadingLanguage.Should().BeNull();

        // Same mix at both ends, three times the lines: every share is unchanged.
        GivenRepositories(GivenRepository("acme", "steady",
            Commit(daysAgo: 400, totalLines: 1_000, byFileType: new Dictionary<string, int>
            {
                [".cs"] = 700,
                [".razor"] = 300
            }),
            Commit(daysAgo: 1, totalLines: 3_000, byFileType: new Dictionary<string, int>
            {
                [".cs"] = 2_100,
                [".razor"] = 900
            })));

        var steady = await WhenQueried();
        steady.LanguageDrift.Should().BeEmpty("a language holding exactly its share is not drift");
    }

    // ─── Streaks and the heatmap ─────────────────────────────────────────────

    [Fact]
    public async Task StreaksAndActivity_CalculatesRunsAndIncludesZeroCommitDays()
    {
        GivenRepositories(GivenRepository("acme", "missed",
            Commit(daysAgo: 5, totalLines: 100),
            Commit(daysAgo: 4, totalLines: 200)));

        var zeroStreak = await WhenQueried();
        zeroStreak.CurrentStreakDays.Should().Be(0);

        GivenRepositories(GivenRepository("acme", "api",
            Commit(daysAgo: 203, totalLines: 10),
            Commit(daysAgo: 202, totalLines: 20),
            Commit(daysAgo: 201, totalLines: 30),
            Commit(daysAgo: 200, totalLines: 40),
            Commit(daysAgo: 2, totalLines: 50),
            Commit(daysAgo: 1, totalLines: 60)));

        var streaks = await WhenQueried();
        streaks.LongestStreakDays.Should().Be(4);
        streaks.CurrentStreakDays.Should().Be(2);

        GivenRepositories(GivenRepository("acme", "api2",
            Commit(daysAgo: 400, totalLines: 50, linesAdded: 50),
            Commit(daysAgo: 45, totalLines: 100, linesAdded: 50),
            Commit(daysAgo: 3, totalLines: 150, linesAdded: 25),
            Commit(daysAgo: 3, totalLines: 175, linesAdded: 15),
            Commit(daysAgo: 1, totalLines: 235, linesAdded: 60)));

        var activity = await WhenQueried();
        // From the first commit (400 days ago) through today, so the heatmap's "All" range has the
        // whole history; a portfolio younger than a year still gets the full year of cells.
        activity.Activity.Should().HaveCount(401);
        streaks.Activity.Should().HaveCount(365);
        activity.Activity.Select(a => a.Date).Should().BeInAscendingOrder();
        activity.Activity.Should().OnlyHaveUniqueItems(a => a.Date);
        activity.Activity.Count(a => a.Commits > 0).Should().Be(4);
        // The streaks read the whole history: the 400-day-old commit is an active day like the rest.
        activity.LongestStreakDays.Should().Be(1);
        activity.ActiveDaysAllTime.Should().Be(4);
        activity.Activity.Single(a => a.Date == Today.AddDays(-1)).LinesAdded.Should().Be(60);
        activity.Commits30Days.Should().Be(3);
        activity.ActiveDays30.Should().Be(2);
        activity.LinesAdded30Days.Should().Be(100);
    }
}
