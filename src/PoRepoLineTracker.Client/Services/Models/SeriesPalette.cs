namespace PoRepoLineTracker.Client.Models;

/// <summary>
/// The categorical chart palette, as CSS custom-property references.
///
/// <para>There is exactly one definition of these twelve colours and it is the
/// <c>--series-0 … --series-11</c> block in <c>app.css</c>. This class does not restate the hex
/// values; it emits <c>var(--series-N)</c>, so a Radzen series, the legend swatch beside it and
/// the progress bar in the summary grid all resolve to the same declaration.</para>
///
/// <para>Three copies used to exist: a 12-colour array in <c>AllReposComparisonChart</c>, an
/// 8-colour array in <c>ContributorChart</c>, and the tokens in the stylesheet. The two arrays
/// carried a comment asserting that Radzen's <c>Stroke</c>/<c>Fill</c> parameters "are attribute
/// content, not CSS — so they cannot be var(--series-N)". That is not correct: an SVG
/// presentation attribute is parsed as a CSS declaration, so <c>var()</c> resolves in it the same
/// way it does in a style rule. <c>RepositoryDetail</c> had in fact been shipping
/// <c>Stroke="var(--rz-primary)"</c> against that very claim the whole time.</para>
///
/// <para>Because the colours now come from the cascade, they also follow a theme change for free
/// rather than being frozen at whatever the light palette was.</para>
/// </summary>
public static class SeriesPalette
{
    /// <summary>Number of distinct hues before the ramp repeats.</summary>
    public const int Length = 12;

    /// <summary>
    /// The CSS reference for a slot, wrapping around at <see cref="Length"/>. Negative indices are
    /// folded to positive first — <see cref="StableIndex"/> can hand back the low bits of a hash,
    /// and a negative slot would otherwise emit <c>var(--series--3)</c>, which resolves to nothing
    /// and paints the series black.
    /// </summary>
    public static string Color(int index) => $"var(--series-{((index % Length) + Length) % Length})";

    /// <summary>The BEM modifier for a legend swatch or bar in the same slot.</summary>
    public static string ModifierClass(string block, int index) =>
        $"{block}--{((index % Length) + Length) % Length}";

    /// <summary>
    /// A slot derived from a name, stable across page loads and machines.
    ///
    /// <para><see cref="string.GetHashCode()"/> cannot be used for this. .NET randomises string
    /// hashing per process, so an author's colour was reshuffled on every reload — the contributor
    /// who was teal in one session was orange in the next, and the chart, its legend and the
    /// summary-grid bar only ever agreed with each other because all three called the same
    /// method within a single process. FNV-1a is a fixed function of the characters, so a given
    /// author keeps a given colour.</para>
    /// </summary>
    public static int StableIndex(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;

        // FNV-1a, 32-bit. Unchecked because the multiply is meant to wrap.
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            var hash = offsetBasis;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= prime;
            }

            return (int)(hash % Length);
        }
    }
}
