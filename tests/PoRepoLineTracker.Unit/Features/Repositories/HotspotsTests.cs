using FluentAssertions;
using PoRepoLineTracker.Client.Components.Repositories;

namespace PoRepoLineTracker.Unit.Features.Repositories;

public class HotspotsTests
{
    [Fact]
    public void ParseNumstat_counts_commits_and_churn_per_path_and_skips_binary_counts()
    {
        const string output = "\u001e\n\n12\t3\tsrc/A.cs\n-\t-\tassets/logo.png\n\u001e\n\n1\t1\tsrc/A.cs\n4\t0\tsrc/B.cs\n\u001e\n";

        var churn = GitClient.ParseNumstat(output);

        churn["src/A.cs"].Should().Be(new FileChurn(2, 17));
        churn["src/B.cs"].Should().Be(new FileChurn(1, 4));
        churn["assets/logo.png"].Should().Be(new FileChurn(1, 0));
        churn.Should().HaveCount(3);
    }

    [Fact]
    public void Rank_orders_by_commits_times_size_and_drops_files_not_changed_in_the_window()
    {
        static SourceFile Lines(string path, int count) =>
            new(path, ".cs", string.Join('\n', Enumerable.Range(0, count).Select(i => $"var x{i} = {i};")));

        var files = new[] { Lines("Big.cs", 100), Lines("Busy.cs", 10), Lines("Hot.cs", 60), Lines("Untouched.cs", 500) };
        var churn = new Dictionary<string, FileChurn>
        {
            ["Big.cs"] = new(1, 5),      // 100
            ["Busy.cs"] = new(20, 40),   // 200
            ["Hot.cs"] = new(8, 90),     // 480
            ["Deleted.cs"] = new(9, 9)   // not at the commit any more
        };

        var ranked = GetRepositoryHotspotsQueryHandler.Rank(files, churn);

        ranked.Select(f => f.Path).Should().Equal("Hot.cs", "Busy.cs", "Big.cs");
        ranked[0].CodeLines.Should().Be(60);
        ranked[0].Commits.Should().Be(8);
    }

    [Fact]
    public void Treemap_tiles_every_value_inside_the_box_with_areas_proportional_to_value()
    {
        double[] values = [500, 300, 120, 50, 20, 8, 2];
        const double width = 1000, height = 560;

        var tiles = HotspotTreemap.Layout(values, width, height);

        tiles.Select(t => t.Index).Should().BeEquivalentTo(Enumerable.Range(0, values.Length));
        var total = values.Sum();
        foreach (var tile in tiles)
        {
            tile.X.Should().BeGreaterThanOrEqualTo(-0.001);
            tile.Y.Should().BeGreaterThanOrEqualTo(-0.001);
            (tile.X + tile.W).Should().BeLessThanOrEqualTo(width + 0.001);
            (tile.Y + tile.H).Should().BeLessThanOrEqualTo(height + 0.001);
            (tile.W * tile.H).Should().BeApproximately(width * height * values[tile.Index] / total, 0.01);
        }

        // Squarified, not sliced: the biggest tile is not a sliver.
        var biggest = tiles.Single(t => t.Index == 0);
        (Math.Max(biggest.W, biggest.H) / Math.Min(biggest.W, biggest.H)).Should().BeLessThan(3);

        HotspotTreemap.Layout([], width, height).Should().BeEmpty();
    }
}
