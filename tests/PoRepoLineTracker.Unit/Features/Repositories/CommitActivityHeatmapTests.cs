using FluentAssertions;
using PoRepoLineTracker.Api.Features.Repositories;
using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Unit.Features.Repositories;

public class CommitActivityHeatmapTests
{
    [Fact]
    public void AggregatePunchcard_correctly_buckets_commits_by_day_and_hour()
    {
        var repoId = new RepositoryId(Guid.NewGuid());
        // Wednesday at 14:00 (2 PM)
        var wednesday = new DateTime(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
        wednesday.DayOfWeek.Should().Be(DayOfWeek.Wednesday);

        var commits = new List<CommitLineCount>
        {
            new()
            {
                RepositoryId = repoId,
                CommitSha = "sha1",
                CommitDate = wednesday,
                LinesAdded = 100,
                LinesRemoved = 20
            },
            new()
            {
                RepositoryId = repoId,
                CommitSha = "sha2",
                CommitDate = wednesday.AddMinutes(15), // Still Wednesday 14:00
                LinesAdded = 50,
                LinesRemoved = 10
            },
            // Sunday at 09:00 (9 AM)
            new()
            {
                RepositoryId = repoId,
                CommitSha = "sha3",
                CommitDate = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc),
                LinesAdded = 300,
                LinesRemoved = 5
            }
        };

        var result = GetRepositoryPunchcardQueryHandler.AggregatePunchcard(commits);

        // 7 days x 24 hours = 168 cells
        result.Should().HaveCount(168);

        // Wednesday (DayOfWeek = 3), Hour = 14
        var wedSlot = result.Single(r => r.DayOfWeek == (int)DayOfWeek.Wednesday && r.HourOfDay == 14);
        wedSlot.CommitCount.Should().Be(2);
        wedSlot.LinesAdded.Should().Be(150);
        wedSlot.LinesRemoved.Should().Be(30);

        // Sunday (DayOfWeek = 0), Hour = 9
        var sunSlot = result.Single(r => r.DayOfWeek == (int)DayOfWeek.Sunday && r.HourOfDay == 9);
        sunSlot.CommitCount.Should().Be(1);
        sunSlot.LinesAdded.Should().Be(300);
        sunSlot.LinesRemoved.Should().Be(5);

        // Empty slot
        var emptySlot = result.Single(r => r.DayOfWeek == (int)DayOfWeek.Monday && r.HourOfDay == 0);
        emptySlot.CommitCount.Should().Be(0);
        emptySlot.LinesAdded.Should().Be(0);
    }
}

