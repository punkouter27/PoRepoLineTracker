namespace PoRepoLineTracker.Shared.Domain;

/// <summary>
/// The one definition of "how many days in a row did I commit".
///
/// <para><b>Why this is not private to a handler.</b> It started as two private methods on the
/// portfolio insights handler. Two separate surfaces print a streak — the Insights dashboard
/// and Year in Review — and slices are forbidden from referencing each other, so the next surface to want a
/// streak would have no shared home to reach for. Without one
/// home each would have grown its own copy, and a streak that reads 12 on one page and 11 on
/// another is indistinguishable from a data bug. This is the same reasoning that put
/// <see cref="RepositoryTotals"/> here.</para>
///
/// <para>Both methods take the set of days that had at least one commit, so the caller decides
/// what counts as activity and over what span. They are pure set arithmetic over dates.</para>
/// </summary>
public static class CommitStreaks
{
    /// <summary>
    /// Consecutive active days ending today — or ending yesterday, since a day with no commits
    /// <i>yet</i> should not read as having broken a streak before it is over.
    /// </summary>
    /// <param name="activeDates">Dates (at midnight) with at least one commit.</param>
    /// <param name="today">The day to count back from, at midnight.</param>
    public static int Current(IReadOnlySet<DateTime> activeDates, DateTime today)
    {
        var cursor = activeDates.Contains(today) ? today : today.AddDays(-1);

        var streak = 0;
        while (activeDates.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        return streak;
    }

    /// <summary>Longest run of consecutive active days anywhere in <paramref name="activeDates"/>.</summary>
    public static int Longest(IReadOnlySet<DateTime> activeDates)
    {
        var longest = 0;
        foreach (var date in activeDates)
        {
            // Only count from the start of a run, so each run is walked once rather than once per
            // day it contains.
            if (activeDates.Contains(date.AddDays(-1))) continue;

            var length = 0;
            for (var cursor = date; activeDates.Contains(cursor); cursor = cursor.AddDays(1))
                length++;

            if (length > longest) longest = length;
        }
        return longest;
    }
}
