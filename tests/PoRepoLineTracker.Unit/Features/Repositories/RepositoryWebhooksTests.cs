using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace PoRepoLineTracker.Unit.Features.Repositories;

public class RepositoryWebhooksTests
{
    // GitHub's own worked example from "Validating webhook deliveries".
    private const string Secret = "It's a Secret to Everybody";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("Hello, World!");
    private const string Signature = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    [Fact]
    public void Accepts_githubs_documented_signature() =>
        RepositoryWebhooks.VerifySignature(Body, Signature, Secret).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e18")]
    [InlineData("sha256=nothex")]
    [InlineData("sha256=7571")]
    public void Rejects_a_missing_or_wrong_signature(string? header) =>
        RepositoryWebhooks.VerifySignature(Body, header, Secret).Should().BeFalse();

    [Fact]
    public void Rejects_when_the_secret_differs() =>
        RepositoryWebhooks.VerifySignature(Body, Signature, "another secret").Should().BeFalse();

    [Fact]
    public async Task A_push_queues_an_incremental_run_not_a_full_one()
    {
        var queue = new AnalysisQueue(Substitute.For<IServiceScopeFactory>(), NullLogger<AnalysisQueue>.Instance);
        var data = Substitute.For<IRepositoryDataService>();
        var repository = new GitHubRepository { Id = RepositoryId.New(), Owner = "o", Name = "r" };

        (await queue.EnqueueReanalysisAsync(repository, data, full: false)).Should().BeTrue();

        // Stamped for restart, and the stamp keeps the incremental intent.
        await data.Received(1).UpdateRepositoryAsync(Arg.Is<GitHubRepository>(r => r.AnalysisQueuedUtc != null && !r.QueuedFullReanalysis));
    }
}
