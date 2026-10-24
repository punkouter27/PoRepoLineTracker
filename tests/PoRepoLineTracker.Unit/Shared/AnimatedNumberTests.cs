using FluentAssertions;
using PoRepoLineTracker.Client.Components.Shared;

namespace PoRepoLineTracker.Unit.Shared;

public class AnimatedNumberTests
{
    [Theory]
    [InlineData("1,234", "1,235", 1)]
    [InlineData("999", "1,000", 4)]   // three reels roll and a fourth appears; the new comma is not a digit
    [InlineData("1,000", "1,000", 0)]
    public void ChangedDigits_counts_reels_that_move_compared_from_the_right(string before, string after, int expected) =>
        AnimatedNumber.ChangedDigits(before, after).Should().Be(expected);

    [Theory]
    [InlineData(999, 1_000, true)]
    [InlineData(9_500, 120_000, true)]
    [InlineData(1_000, 1_999, false)]
    [InlineData(2_000_000, 999, false)]   // a fall is never a milestone
    [InlineData(10, 900, false)]          // below a thousand nothing counts
    public void CrossedMilestone_is_a_rise_past_a_power_of_ten_from_a_thousand(int before, int after, bool expected) =>
        AnimatedNumber.CrossedMilestone(before, after).Should().Be(expected);
}
