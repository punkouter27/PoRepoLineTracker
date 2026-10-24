using FluentAssertions;
using PoRepoLineTracker.API.Features.Insights;

namespace PoRepoLineTracker.Unit;

public class YearInReviewTests
{
    private static CommitLineCount Commit(DateTime utc, int added, int total, Dictionary<string, int>? mix = null) => new()
    {
        Id = Guid.NewGuid(),
        CommitSha = Guid.NewGuid().ToString("N"),
        CommitDate = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
        LinesAdded = added,
        TotalLines = total,
        LinesByFileType = mix ?? []
    };

    [Fact]
    public void Build_reports_the_local_year_with_snapshot_totals_streaks_and_share_growth()
    {
        var api = new GitHubRepository { Id = RepositoryId.New(), Name = "api" };
        var site = new GitHubRepository { Id = RepositoryId.New(), Name = "site" };
        var data = new List<(GitHubRepository, IReadOnlyList<CommitLineCount>)>
        {
            (api, [
                Commit(new(2025, 12, 20, 12, 0, 0), 100, 1000, new() { [".cs"] = 1000 }),     // before the year: the start snapshot
                Commit(new(2026, 3, 1, 22, 0, 0), 50, 1050, new() { [".cs"] = 1050 }),
                Commit(new(2026, 3, 2, 22, 0, 0), 400, 1450, new() { [".cs"] = 1050, [".ts"] = 400 }),
                Commit(new(2026, 3, 3, 22, 0, 0), 10, 1460, new() { [".cs"] = 1060, [".ts"] = 400 }),
            ]),
            (site, [
                Commit(new(2026, 7, 4, 9, 0, 0), 20, 20, new() { [".css"] = 20 }),
                // 02:00 UTC on 1 Jan 2027 is still 2026 at UTC-5, so it belongs to this year.
                Commit(new(2027, 1, 1, 2, 0, 0), 5, 25, new() { [".css"] = 25 }),
            ])
        };

        var review = GetYearInReviewQueryHandler.Build(data, 2026, TimeSpan.FromHours(-5));

        review.HasActivity.Should().BeTrue();
        review.Commits.Should().Be(5);
        review.LinesAdded.Should().Be(485);
        review.RepositoriesTouched.Should().Be(2);
        review.TopRepository.Should().Be("api");
        review.TopRepositoryCommits.Should().Be(3);
        review.LongestStreakDays.Should().Be(3);
        review.BusiestMonth.Should().Be(3);
        review.PeakHour.Should().Be(17, "22:00 UTC is 17:00 at UTC-5");
        review.BiggestDayLinesAdded.Should().Be(400);
        review.BiggestDay.Should().Be(new DateTime(2026, 3, 2));
        // Snapshots, never sums: 1000 at the start; 1460 + 25 at the end.
        review.PortfolioLinesStart.Should().Be(1000);
        review.PortfolioLinesEnd.Should().Be(1485);
        review.TopGrowingExtension.Should().Be(".ts");
        review.AvailableYears.Should().Equal(2026, 2025);
        // One slot per local day: the New Year's Eve commit is 31 December's, not 1 January's.
        review.DailyCommits.Should().HaveCount(365);
        review.DailyCommits.Sum().Should().Be(5);
        review.DailyCommits[new DateTime(2026, 3, 1).DayOfYear - 1].Should().Be(1);
        review.DailyCommits[364].Should().Be(1);
    }

    [Fact]
    public void Build_for_a_quiet_year_has_no_activity_but_still_lists_the_years()
    {
        var repo = new GitHubRepository { Id = RepositoryId.New(), Name = "old" };
        var data = new List<(GitHubRepository, IReadOnlyList<CommitLineCount>)> { (repo, [Commit(new(2024, 5, 5), 10, 10)]) };

        var review = GetYearInReviewQueryHandler.Build(data, 2026, TimeSpan.Zero);

        review.HasActivity.Should().BeFalse();
        review.MonthlyCommits.Should().HaveCount(12).And.OnlyContain(c => c == 0);
        review.AvailableYears.Should().Equal(2024);
    }
}
