namespace PoRepoLineTracker.Client.Layout;

/// <summary>
/// Shared dev persona definitions for the header X-Fake-User switcher outside Production (Rule 16).
/// </summary>
public static class AccountMenu
{
    public sealed record DevPersona(string Label, string? FakeUserHeader);

    public static readonly IReadOnlyList<DevPersona> Personas =
    [
        new("Cookie Session", null),
        new("e2e-ui", "e2e-ui"),
        new("alice", "alice"),
    ];
}
