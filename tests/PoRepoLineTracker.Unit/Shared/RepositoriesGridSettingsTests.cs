using FluentAssertions;
using PoRepoLineTracker.Client.Components.Repositories;
using PoRepoLineTracker.Client.Models;

namespace PoRepoLineTracker.Unit.Shared;

/// <summary>
/// The grid's saved settings are a hand-rolled string in localStorage (see RepositoriesGrid for
/// why not JSON), so the parser is the trust boundary: whatever is in there must not reach the
/// grid as a sort property it cannot resolve.
/// </summary>
public class RepositoriesGridSettingsTests
{
    [Fact]
    public void Settings_round_trip_and_drop_what_the_grid_does_not_know()
    {
        var saved = RepositoriesGrid.FormatSettings("TotalLinesSortValue", descending: true, ["Net (30d)", "Status"]);
        var (sort, descending, hidden) = RepositoriesGrid.ParseSettings(saved);
        sort.Should().Be("TotalLinesSortValue");
        descending.Should().BeTrue();
        hidden.Should().BeEquivalentTo("Net (30d)", "Status");

        // Nothing sorted, nothing hidden.
        var empty = RepositoriesGrid.ParseSettings(RepositoriesGrid.FormatSettings(null, false, []));
        empty.SortProperty.Should().BeNull();
        empty.Hidden.Should().BeEmpty();

        // Tampered or from an older build: unknown names are ignored, short input does not throw.
        var (badSort, _, badHidden) = RepositoriesGrid.ParseSettings("Repository.CloneUrl|desc|Actions,Status,");
        badSort.Should().BeNull();
        badHidden.Should().BeEquivalentTo("Status");
        RepositoriesGrid.ParseSettings("").Hidden.Should().BeEmpty();
        RepositoriesGrid.ParseSettings(null).SortProperty.Should().BeNull();
    }

    [Fact]
    public void RangeDays_accepts_only_the_offered_ranges_and_round_trips_through_the_url()
    {
        (string? Query, int Expected)[] cases =
            [("365", 365), ("all", ChartFormat.AllDays), ("180", 180), ("30", 180), ("abc", 180), (null, 180)];

        foreach (var (query, expected) in cases)
            ChartFormat.RangeDays(query).Should().Be(expected, $"for '{query}'");

        // The default stays out of the URL; the other two come back as what they were written as.
        ChartFormat.RangeQuery(ChartFormat.DefaultRangeDays).Should().BeNull();
        foreach (var days in new[] { 365, ChartFormat.AllDays })
            ChartFormat.RangeDays(ChartFormat.RangeQuery(days)).Should().Be(days);

        // A page that opens on "All" (Repositories) leaves that out instead, and writes 180 down.
        ChartFormat.RangeDays(null, ChartFormat.AllDays).Should().Be(ChartFormat.AllDays);
        ChartFormat.RangeQuery(ChartFormat.AllDays, ChartFormat.AllDays).Should().BeNull();
        foreach (var days in new[] { ChartFormat.DefaultRangeDays, 365, ChartFormat.AllDays })
            ChartFormat.RangeDays(ChartFormat.RangeQuery(days, ChartFormat.AllDays), ChartFormat.AllDays).Should().Be(days);
    }
}
