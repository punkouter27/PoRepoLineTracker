namespace PoRepoLineTracker.API.Features;

internal static class SettingsEndpoints
{
    internal static void MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // /api/settings is entirely per-user state, so the group requires auth. Under the
        // FallbackPolicy these routes would be protected anyway; declaring it on the group
        // makes the intent explicit and survives a future change to the fallback.
        var settings = endpoints.MapGroup("/api/settings")
            .WithTags("Settings")
            .RequireAuthorization();

        settings.MapGet("/user-preferences", async (HttpContext ctx, IUserPreferencesService preferencesService) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var preferences = await preferencesService.GetPreferencesAsync(userId);
            return Results.Ok(preferences);
        })
        .WithName("GetUserPreferences");

        settings.MapPut("/user-preferences", async (HttpContext ctx, IUserPreferencesService preferencesService, UserPreferences preferences) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            // Extensions are stored joined with ',' and globs with ';' (UserPreferencesEntity),
            // so a value containing its separator silently splits into two on the way back;
            // an unbounded list overflows the table property and answers 500.
            if (preferences.FileExtensions is { Count: > 200 }
                || preferences.FileExtensions?.Any(e => e is null || e.Length is 0 or > 32 || e.Contains(',')) == true
                || preferences.CustomIgnoreGlobs is { Count: > 100 }
                || preferences.CustomIgnoreGlobs?.Any(g => g is null || g.Length is 0 or > 200 || g.Contains(';')) == true)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["preferences"] = ["At most 200 extensions (32 characters, no ',') and 100 ignore patterns (200 characters, no ';')."]
                });
            }

            preferences = preferences with { UserId = userId, LastUpdated = DateTime.UtcNow };
            await preferencesService.SavePreferencesAsync(preferences);
            return Results.Ok(preferences);
        })
        .WithName("SaveUserPreferences");
    }
}
