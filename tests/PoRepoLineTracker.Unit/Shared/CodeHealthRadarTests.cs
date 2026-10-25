using FluentAssertions;
using PoRepoLineTracker.Client.Components.Charts;

namespace PoRepoLineTracker.Unit.Shared;

public class CodeHealthRadarTests
{
    [Fact]
    public void RadarGeometry_distributes_angles_and_clamps_points()
    {
        CodeHealthRadar.GetAngle(0, 6).Should().BeApproximately(-Math.PI / 2.0, 0.001);
        CodeHealthRadar.GetAngle(1, 6).Should().BeApproximately(-Math.PI / 6.0, 0.001);

        const double cx = 100;
        const double cy = 100;
        const double radius = 50;

        // Score 0 should be exactly at center
        var (zeroX, zeroY) = CodeHealthRadar.CalculatePoint(cx, cy, radius, 0, 0, 6);
        zeroX.Should().BeApproximately(cx, 0.001);
        zeroY.Should().BeApproximately(cy, 0.001);

        // Score 100 at index 0 (top) should be at (cx, cy - radius)
        var (hundredX, hundredY) = CodeHealthRadar.CalculatePoint(cx, cy, radius, 100, 0, 6);
        hundredX.Should().BeApproximately(cx, 0.001);
        hundredY.Should().BeApproximately(cy - radius, 0.001);

        // Score > 100 should clamp to 100
        var (overX, overY) = CodeHealthRadar.CalculatePoint(cx, cy, radius, 150, 0, 6);
        overX.Should().BeApproximately(cx, 0.001);
        overY.Should().BeApproximately(cy - radius, 0.001);

        // Negative score should clamp to 0 (center)
        var (negX, negY) = CodeHealthRadar.CalculatePoint(cx, cy, radius, -20, 0, 6);
        negX.Should().BeApproximately(cx, 0.001);
        negY.Should().BeApproximately(cy, 0.001);
    }
}

