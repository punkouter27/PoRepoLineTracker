using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PoRepoLineTracker.Shared.Models;

namespace PoRepoLineTracker.Api.Hubs;

/// <summary>
/// Pushes live analysis progress to the browser.
///
/// <para><b>Why this replaces polling.</b> The Repositories page previously asked
/// <c>/analysis-progress</c> for every pending repository on a five-second timer, plus a full
/// <c>/api/repositories</c> read on each tick — so a page with ten pending repositories issued
/// eleven requests every five seconds and still showed each step up to five seconds late. The
/// server already knows the instant a step changes; this lets it say so.</para>
///
/// <para><b>Isolation.</b> Connections are placed in a group derived from the authenticated
/// principal — never from anything the client sends. A caller therefore cannot subscribe to
/// another user's jobs by guessing a repository id, which is the failure mode a
/// client-supplied "subscribe to this repo" method would have had to defend against on every
/// call.</para>
/// </summary>
[Authorize]
public sealed class AnalysisHub : Hub
{
    /// <summary>
    /// Group name for a user's own jobs. Kept here rather than at the broadcast site so the
    /// producer and the consumer of the name cannot drift apart.
    /// </summary>
    public static string GroupFor(UserId userId) => $"user:{userId}";

    /// <summary>Name of the client-side handler; referenced by <c>AnalysisProgressService</c>.</summary>
    public const string ProgressMethod = "AnalysisProgress";

    /// <summary>
    /// Options for the bounded channel that sits between the analysis loop and the SignalR
    /// reader. Capacity 64, <see cref="BoundedChannelFullMode.DropOldest"/>, single reader.
    ///
    /// <para><b>ponytail:</b> the <c>DropOldest</c> policy is the deliberate corner-cut named
    /// in SPEC §10. The reader is slower than the writer only when a SignalR client is wedged;
    /// the recovery path is the fallback poll in <c>Repositories.razor</c>, which synthesises
    /// the same frames from the persisted snapshot, so a lost frame costs at most one stale
    /// tick. Upgrade path: switch to <see cref="BoundedChannelFullMode.Wait"/> if loss ever
    /// becomes observable to a real client.</para>
    /// </summary>
    public static BoundedChannelOptions ProgressChannelOptions { get; } = new(capacity: 64)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        AllowSynchronousContinuations = false,
    };

    /// <summary>
    /// The bounded channel itself. Static so the producer (the analysis loop running inside
    /// <see cref="Services.AnalysisProgressService"/>) and the consumer
    /// (<see cref="AnalysisProgressReader"/>) share one path.
    /// </summary>
    public static Channel<(UserId Owner, AnalysisProgressDto Frame)> ProgressChannel { get; } =
        Channel.CreateBounded<(UserId Owner, AnalysisProgressDto Frame)>(ProgressChannelOptions);

    public override async Task OnConnectedAsync()
    {
        if (Context.User is not null && Context.User.TryGetUserId(out var userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId));
        }
        else
        {
            // [Authorize] guarantees an identity, but not that it carries the claim this app keys
            // on. Aborting is the safe reading: leaving the connection open would produce a client
            // that looks connected and silently receives nothing.
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }
}
