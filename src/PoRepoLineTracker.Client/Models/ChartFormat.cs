namespace PoRepoLineTracker.Client.Models;

/// <summary>
/// Display formatting shared by the charts and grids.
/// <para>
/// Both helpers were previously duplicated verbatim: <c>FormatLines</c> as a private method on
/// both <c>AllReposComparisonChart</c> and <c>RepositoryDetail</c>, and <c>ToRelativeTime</c> on
/// both <c>Repositories</c> and <c>RepositoryDetail</c>. Two copies of an axis formatter drift,
/// and an axis that rounds differently from the grid beside it reads as a data bug.
/// </para>
/// </summary>
public static class ChartFormat
{
    /// <summary>
    /// Radzen axis formatter overload. Radzen hands tick values through as <see cref="object"/>
    /// (boxed <see cref="double"/>), so the signature has to accept one.
    /// </summary>
    public static string Lines(object value) =>
        value is double d ? Lines(d) : value?.ToString() ?? string.Empty;

    /// <summary>Compact line counts for axis ticks: 1_500 → "2k", 1_500_000 → "1.5M".</summary>
    public static string Lines(double value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000:F1}M";
        if (value >= 1_000) return $"{value / 1_000:F0}k";
        return value.ToString("N0");
    }

    /// <summary>
    /// A <c>?days=</c> query value as one of the ranges the selectors offer, else
    /// <see cref="DefaultRangeDays"/>. Taken as a string and matched, not bound as an int: the router
    /// throws on a query value it cannot parse, so a hand-edited URL would take the page down, and
    /// the number goes straight into an API path.
    /// </summary>
    public static int RangeDays(string? query) => query switch
    {
        "365" => 365,
        "all" => AllDays,
        _ => DefaultRangeDays
    };

    /// <summary>The range a chart opens on, and the one left out of the URL.</summary>
    public const int DefaultRangeDays = 180;

    /// <summary>
    /// "All": a window longer than any repository's history. The endpoints take a day count, so
    /// "everything" is simply a count nothing can exceed (100 years).
    /// </summary>
    public const int AllDays = 36_500;

    /// <summary>The <c>?days=</c> value for a range; null (omitted) at the default.</summary>
    public static string? RangeQuery(int days) => days switch
    {
        365 => "365",
        AllDays => "all",
        _ => null
    };

    /// <summary>A signed line-count change: "+1,234", "-56", "no change". One copy for Insights and the grid.</summary>
    public static string Delta(int value) => value switch
    {
        > 0 => $"+{value:N0}",
        < 0 => value.ToString("N0"),
        _ => "no change"
    };

    /// <summary>Coarse "time ago" label. Empty string for a null date.</summary>
    public static string RelativeTime(DateTime? value)
    {
        if (!value.HasValue) return string.Empty;

        var elapsed = DateTime.UtcNow - value.Value.ToUniversalTime();
        if (elapsed.TotalHours < 1) return "just now";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        if (elapsed.TotalDays < 30) return $"{(int)(elapsed.TotalDays / 7)}w ago";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)}mo ago";
        return $"{(int)(elapsed.TotalDays / 365)}y ago";
    }

    /// <summary>
    /// Tick spacing for a date axis, as a whole number of days.
    /// <para>
    /// Both of the obvious alternatives produce duplicate labels. Leaving the axis to its own
    /// devices draws one tick per day, so a 365-day range collapses into an unreadable smear.
    /// Setting <c>TickDistance</c> (a pixel gap) fixes the count but not the positions: with only
    /// eight days of data in a wide card, Radzen spaces ticks less than a day apart and the
    /// "MMM d" format renders them as "Jul 21, Jul 21, Jul 22, Jul 22". Quantising the step to
    /// whole days is what makes every label distinct.
    /// </para>
    /// <para>
    /// Derived from the data's own span rather than the selected range, because the two differ:
    /// a repository analysed last week has eight days of history behind a 90-day selection.
    /// </para>
    /// </summary>
    /// <param name="dates">Plotted dates. Fewer than two yields a one-day step.</param>
    /// <param name="targetTicks">Approximate number of labels to aim for.</param>
    public static TimeSpan DateAxisStep(IEnumerable<DateTime>? dates, int targetTicks = 8)
    {
        if (dates is null) return TimeSpan.FromDays(1);

        var (min, max, count) = (DateTime.MaxValue, DateTime.MinValue, 0);
        foreach (var date in dates)
        {
            if (date < min) min = date;
            if (date > max) max = date;
            count++;
        }

        if (count < 2) return TimeSpan.FromDays(1);

        var spanDays = (max - min).TotalDays;
        return TimeSpan.FromDays(Math.Max(1, Math.Ceiling(spanDays / Math.Max(1, targetTicks))));
    }

    /// <summary>
    /// A streak flame's height for <c>.u-flame</c>'s <c>--flame</c>, 0-1, full at 30 days. Invariant,
    /// so a comma-decimal culture cannot write "0,5" into the CSS.
    /// </summary>
    public static string StreakFlame(int streakDays) =>
        Math.Clamp(streakDays / 30.0, 0, 1).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
