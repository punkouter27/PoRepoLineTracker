using FluentAssertions;
using PoRepoLineTracker.Api.Features.Insights;

namespace PoRepoLineTracker.Unit;

public class CommitOutliersTests
{
    private static readonly GitHubRepository Repo = new() { Id = RepositoryId.New(), Owner = "acme", Name = "api" };
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly int[] OldSpikeSeed = [40_000];

    private static List<CommitLineCount> History(params int[] linesAdded) =>
        linesAdded.Select((lines, i) => new CommitLineCount
        {
            CommitSha = $"sha{i:D5}abcdef",
            CommitDate = Now.AddDays(-linesAdded.Length + i + 1),
            LinesAdded = lines
        }).ToList();

    [Fact]
    public void Flags_TheVendoredDump_AndNothingElse()
    {
        // 30 ordinary commits of 20-80 lines, then one 40,000-line commit yesterday.
        var sizes = Enumerable.Range(0, 30).Select(i => 20 + i * 2).Append(40_000).ToArray();

        var outliers = CommitOutliers.Find(Repo, History(sizes), Now.AddDays(-30)).ToList();

        outliers.Should().ContainSingle();
        outliers[0].LinesAdded.Should().Be(40_000);
        outliers[0].Name.Should().Be("api");
        outliers[0].CommitSha.Should().HaveLength(7);
        outliers[0].TypicalLinesAdded.Should().BeInRange(20, 80);
    }

    [Fact]
    public void StaysQuiet_ForShortHistory_SmallSpikes_AndOldCommits()
    {
        // Too little history for "normal" to mean anything.
        CommitOutliers.Find(Repo, History(10, 10, 10, 50_000), Now.AddDays(-30)).Should().BeEmpty();

        // A spike that is large relative to the repository but small in absolute terms.
        var smallSpike = Enumerable.Repeat(3, 30).Append(400).ToArray();
        CommitOutliers.Find(Repo, History(smallSpike), Now.AddDays(-30)).Should().BeEmpty();

        // A genuine outlier, but outside the window being reported on.
        var oldSpike = OldSpikeSeed.Concat(Enumerable.Repeat(30, 60)).ToArray();
        CommitOutliers.Find(Repo, History(oldSpike), Now.AddDays(-30)).Should().BeEmpty();
    }
}
