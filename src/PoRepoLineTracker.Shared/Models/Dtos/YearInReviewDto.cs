namespace PoRepoLineTracker.Shared.Models.Dtos;

/// <summary>
/// One calendar year across all of the user's repositories, for the Year in Review story.
///
/// <para>Days, hours and months are in the viewer's time zone (the client sends its UTC offset):
/// "busiest hour" in UTC would tell someone in Sydney they code at breakfast. Line totals are
/// snapshots (<see cref="Domain.RepositoryTotals"/>), never sums.</para>
/// </summary>
public sealed class YearInReviewDto
{
    public int Year { get; set; }

    /// <summary>Years with at least one commit, newest first, for the year picker.</summary>
    public List<int> AvailableYears { get; set; } = [];

    /// <summary>False when the year had no commits; the page then shows the picker and nothing else.</summary>
    public bool HasActivity { get; set; }

    public int Commits { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int ActiveDays { get; set; }

    /// <summary>Longest run of consecutive active days inside the year (<see cref="Domain.CommitStreaks"/>).</summary>
    public int LongestStreakDays { get; set; }

    /// <summary>Commits per month, January first; always twelve entries.</summary>
    public List<int> MonthlyCommits { get; set; } = [];

    /// <summary>1–12; 0 when there was no activity.</summary>
    public int BusiestMonth { get; set; }

    /// <summary>The single day with the most lines added, and how many.</summary>
    public DateTime? BiggestDay { get; set; }
    public int BiggestDayLinesAdded { get; set; }

    /// <summary>0–23, local: the hour with the most commits.</summary>
    public int PeakHour { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday, local.</summary>
    public int PeakWeekday { get; set; }

    public string? TopRepository { get; set; }
    public int TopRepositoryCommits { get; set; }
    public int RepositoriesTouched { get; set; }

    /// <summary>Portfolio size on the year's first day and its last.</summary>
    public int PortfolioLinesStart { get; set; }
    public int PortfolioLinesEnd { get; set; }

    /// <summary>
    /// The file type whose SHARE of the portfolio grew most over the year (share, not lines, as on
    /// Insights), and by how many percentage points. Null when no type gained share.
    /// </summary>
    public string? TopGrowingExtension { get; set; }
    public double TopGrowingSharePoints { get; set; }
}
