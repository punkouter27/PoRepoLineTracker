using Microsoft.AspNetCore.Components.Routing;

namespace PoRepoLineTracker.Client.Models;

/// <summary>One entry in the sidebar.</summary>
/// <param name="Href">The route to link to.</param>
/// <param name="Label">What the entry is called.</param>
/// <param name="Icon">Material icon name.</param>
/// <param name="Match">
/// <see cref="NavLinkMatch.All"/> by default. <see cref="NavLinkMatch.Prefix"/> for a page that
/// owns child routes, so the entry stays lit while the user is on one of them.
/// </param>
public sealed record NavItem(string Href, string Label, string Icon, NavLinkMatch Match = NavLinkMatch.All);

/// <summary>A run of sidebar entries under an optional heading.</summary>
/// <param name="Heading">The heading, or null for a group that needs none.</param>
/// <param name="Items">The entries, in order.</param>
public sealed record NavGroup(string? Heading, IReadOnlyList<NavItem> Items);

/// <summary>
/// The sidebar, in one place.
/// <para>
/// The list used to be hand-written markup, which meant a new destination had to be added in two
/// places to keep the router and the sidebar in step. Now the router is keyed off
/// <see cref="AppRoutes"/> and this list reads from it, so a route that exists but is not
/// listed is a deliberate omission rather than an oversight.
/// </para>
/// </summary>
public static class AppNav
{
    /// <summary>
    /// The sidebar's groups, in order. Grouped by what the user is doing rather than by feature:
    /// everything you look at, then everything you configure.
    /// </summary>
    public static readonly NavGroup[] Groups =
    [
        new("Portfolio",
        [
            // Prefix: the page owns /repositories/{id}, so the entry stays lit on a detail page.
            new(AppRoutes.Repositories, "Repositories", "storage", NavLinkMatch.Prefix),
            new(AppRoutes.Insights, "All Time Insights", "insights"),
            new(AppRoutes.CodeHealth, "Code Health", "health_and_safety"),
            // Prefix: the page also lives at /year-in-review/{year}.
            new(AppRoutes.YearInReview, "Year in Review", "auto_awesome", NavLinkMatch.Prefix),
        ]),
        new("Configure",
        [
            new(AppRoutes.Settings, "Settings", "tune"),
        ]),
    ];

    // No Diagnostics entry, by the owner's choice: /diagnostics is an operator's tool, not
    // something to offer every signed-in user from the sidebar.
}
