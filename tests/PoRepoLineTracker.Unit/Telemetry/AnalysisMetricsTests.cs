using System.Diagnostics.Metrics;
using FluentAssertions;
using PoRepoLineTracker.Api.Telemetry;
using PoRepoLineTracker.Shared.Domain;

namespace PoRepoLineTracker.Unit.Telemetry;

/// <summary>
/// Pins that <see cref="AnalysisMetrics"/> records into the same <see cref="System.Diagnostics.Metrics.Meter"/>
/// the OpenTelemetry pipeline subscribes to via <c>TelemetryServiceExtensions.AddTelemetry</c>.
///
/// <para>If these were on a fresh <c>Meter</c>, an OTel pipeline built only against
/// <c>AppTelemetry.SourceName</c> would silently drop the samples — and the live view would
/// stop showing "lines counted" without any test failing. This test is the guard against that.</para>
/// </summary>
public class AnalysisMetricsTests
{
    [Fact]
    public void AnalysisMetrics_record_into_the_AppTelemetry_meter()
    {
        var listener = new MeterListener();
        var seenLongs = new List<(long Value, IReadOnlyDictionary<string, object?> Tags)>();
        var seenDoubles = new List<(double Value, IReadOnlyDictionary<string, object?> Tags)>();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AppTelemetry.SourceName &&
                (instrument.Name == AnalysisMetrics.LinesCountedName || instrument.Name == AnalysisMetrics.CommitCountDurationName))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seenLongs.Add((value, ToReadOnly(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            seenDoubles.Add((value, ToReadOnly(tags))));
        listener.Start();

        var repoId = new RepositoryId(Guid.NewGuid());
        AnalysisMetrics.RecordLinesCounted(repoId, 4_242);
        AnalysisMetrics.RecordCommitCountDuration(repoId, 12.5);

        listener.RecordObservableInstruments();
        listener.Dispose();

        // Only this test's repository. The meter is process-wide and the analyze-handler tests
        // record into it from other threads, so an unfiltered list intermittently held their
        // samples too ("expected 4242, found 150").
        var repo = repoId.Value.ToString();
        seenLongs.RemoveAll(s => !Equals(s.Tags.GetValueOrDefault("repo"), repo));
        seenDoubles.RemoveAll(s => !Equals(s.Tags.GetValueOrDefault("repo"), repo));

        seenLongs.Should().ContainSingle();
        seenLongs[0].Value.Should().Be(4_242L);
        seenLongs[0].Tags.Should().ContainKey("repo").WhoseValue.Should().Be(repoId.Value.ToString());

        seenDoubles.Should().ContainSingle();
        seenDoubles[0].Value.Should().Be(12.5);
        seenDoubles[0].Tags["repo"].Should().Be(repoId.Value.ToString());
    }

    private static IReadOnlyDictionary<string, object?> ToReadOnly(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kv in tags) dict[kv.Key] = kv.Value;
        return dict;
    }
}
