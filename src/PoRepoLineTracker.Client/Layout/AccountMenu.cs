using PoRepoLineTracker.Shared.Models;

namespace PoRepoLineTracker.Client.Layout;

/// <summary>
/// Account display for the header and Settings, and the dev persona definitions for the X-Fake-User switcher outside
/// Production (Rule 16; in Settings' account card since the header became the Po shell).
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

    /// <summary>
    /// Who is signed in, as the header shows it: the email GitHub shared (the user:email scope), else the GitHub login.
    /// Never the display name.
    /// </summary>
    public static string Identity(AuthResponse? user) =>
        !string.IsNullOrWhiteSpace(user?.Email) ? user.Email
        : !string.IsNullOrWhiteSpace(user?.Username) ? user.Username
        : "Signed in";
}
