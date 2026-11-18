namespace PoRepoLineTracker.Client.Models;

/// <summary>
/// One value-type for the loading → error → empty → loaded ladder that every fetch in the
/// client renders. Pages used to carry three fields each (<c>isLoading</c>, <c>errorMessage</c>,
/// plus a null check on the data) and the same if/else chain rendered the four states in
/// identical order. Bundling them in a single discriminated record means a page cannot
/// accidentally present an error while the data is also showing, or mark loaded while an
/// error is still on screen — both bugs happened.
/// </summary>
/// <typeparam name="T">The shape of the loaded data.</typeparam>
public abstract record LoadingState<T>
{
    private LoadingState() { }

    /// <summary>Initial state and the only state with a fetch in flight.</summary>
    public sealed record Loading() : LoadingState<T>;

    /// <summary>Fetch returned a non-success response, or threw.</summary>
    /// <param name="Message">User-facing copy. Already localised at the call site if needed.</param>
    public sealed record Failed(string Message) : LoadingState<T>;

    /// <summary>Fetch succeeded but the server returned no rows — distinct from "loading" so the
    /// page can offer an empty-state CTA rather than a spinner.</summary>
    public sealed record Empty : LoadingState<T>;

    /// <summary>Fetch succeeded and the server returned at least one row.</summary>
    public sealed record Loaded : LoadingState<T>
    {
        public T Value { get; }
        public Loaded(T value) { Value = value; }
    }

    /// <summary>True when the page is waiting for a fetch to complete.</summary>
    public bool IsLoading => this is Loading;

    /// <summary>The loaded data, or <c>null</c> if the page is loading, errored, or empty.</summary>
    public T? Data => this is Loaded l ? l.Value : default;

    /// <summary>True when the fetch has finished and there is something to render.</summary>
    public bool HasData => this is Loaded;
}
