using System.Threading.Channels;
using FluentAssertions;
using PoRepoLineTracker.Api.Hubs;
using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models;

namespace PoRepoLineTracker.Unit.Hubs;

/// <summary>
/// Pins the bounded channel contract that <see cref="AnalysisHub.ProgressChannel"/> exposes:
/// capacity 64, <see cref="BoundedChannelFullMode.DropOldest"/>, single reader. Together these
/// mean a slow SignalR consumer cannot stall the analysis loop, and the worst case is loss of
/// the *oldest* frame — the next report supersedes it. This is the corner-cut the SPEC §10
/// "DropOldest is deliberate" rule names.
/// </summary>
public class AnalysisHubChannelTests
{
    [Fact]
    public async Task ProgressChannel_contract_bounded_and_drops_oldest_under_load()
    {
        var options = AnalysisHub.ProgressChannelOptions;
        options.Capacity.Should().Be(64, "the SPEC §10 budget for backlogged progress frames");
        options.FullMode.Should().Be(BoundedChannelFullMode.DropOldest,
            "loss of the oldest frame is recoverable: the next report carries the same step forward");
        options.SingleReader.Should().BeTrue(
            "only AnalysisProgressReader drains it; SingleReader unlocks the SingleReader optimisation");
        options.AllowSynchronousContinuations.Should().BeFalse(
            "the reader runs on its own thread; sync continuations would pin it to the writer");

        var channel = Channel.CreateBounded<AnalysisProgressDto>(AnalysisHub.ProgressChannelOptions);
        var writer = channel.Writer;
        var reader = channel.Reader;

        var repoId = new RepositoryId(Guid.NewGuid());
        for (var i = 0; i < 200; i++)
        {
            var ok = writer.TryWrite(new AnalysisProgressDto
            {
                RepositoryId = repoId,
                StepName = "Processing",
                CommitsProcessed = i,
                CommitsTotal = 1000,
                IsRunning = true,
            });
            ok.Should().BeTrue("TryWrite must not block the producer even when the channel is full");
        }

        writer.TryComplete();
        var received = new List<AnalysisProgressDto>();
        while (await reader.WaitToReadAsync().AsTask())
        {
            while (reader.TryRead(out var frame)) received.Add(frame);
        }

        received.Should().HaveCountLessThanOrEqualTo(64);
        received.Should().NotBeEmpty();
        received.Last().CommitsProcessed.Should().Be(199,
            "the most recent frame is the one that survives under DropOldest — the analysis loop's next report carries the same step forward anyway");
    }
}
