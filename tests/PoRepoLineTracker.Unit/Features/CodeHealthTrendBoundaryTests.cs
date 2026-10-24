using FluentAssertions;
using PoRepoLineTracker.API.Features.CodeHealth;

namespace PoRepoLineTracker.Unit;

/// <summary>Which commit each point of the all-time health line is measured at.</summary>
public class CodeHealthTrendBoundaryTests
{
    private static CommitLineCount Commit(DateTime utc) => new()
    {
        Id = Guid.NewGuid(),
        CommitSha = Guid.NewGuid().ToString("N"),
        CommitDate = DateTime.SpecifyKind(utc, DateTimeKind.Utc)
    };

    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_first_of_the_month_from_the_first_commit_reads_the_last_commit_before_it()
    {
        var january = Commit(new(2020, 1, 15, 12, 0, 0));
        var march = Commit(new(2020, 3, 10, 12, 0, 0));

        var points = GetCodeHealthTrendQueryHandler.ResolveMonthlyCommits([january, march], Utc(2020, 5, 20));

        points.Select(p => p.Month).Should().Equal(Utc(2020, 2, 1), Utc(2020, 3, 1), Utc(2020, 4, 1), Utc(2020, 5, 1));
        points.Select(p => p.Commit).Should().Equal(january, january, march, march);
    }

    [Fact]
    public void A_decade_is_thinned_to_the_cap_keeping_the_first_and_newest_boundaries()
    {
        var points = GetCodeHealthTrendQueryHandler.ResolveMonthlyCommits(
            [Commit(new(2016, 1, 5)), Commit(new(2026, 10, 2))], Utc(2026, 10, 24));

        points.Should().HaveCount(GetCodeHealthTrendQueryHandler.MaxPoints);
        points[0].Month.Should().Be(Utc(2016, 2, 1));
        points[^1].Month.Should().Be(Utc(2026, 10, 1));
        points.Select(p => p.Month).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_history_inside_the_current_month_has_no_boundary_yet()
    {
        GetCodeHealthTrendQueryHandler.ResolveMonthlyCommits([Commit(new(2026, 10, 3))], Utc(2026, 10, 24))
            .Should().BeEmpty();
    }
}
