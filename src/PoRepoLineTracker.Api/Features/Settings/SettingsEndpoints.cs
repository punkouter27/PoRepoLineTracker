using FluentValidation;

namespace PoRepoLineTracker.Api.Features.Settings;

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

        settings.MapPut("/user-preferences", async (HttpContext ctx, IUserPreferencesService preferencesService, IValidator<UserPreferences> validator, UserPreferences preferences) =>
        {
            if (!ctx.User.TryGetUserId(out var userId))
                return Results.Unauthorized();

            // The same rules the Settings page checks before it sends (UserPreferencesValidator).
            if (validator.Validate(preferences) is { IsValid: false } result)
                return Results.ValidationProblem(result.ToDictionary());

            preferences = preferences with { UserId = userId, LastUpdated = DateTime.UtcNow };
            await preferencesService.SavePreferencesAsync(preferences);
            return Results.Ok(preferences);
        })
        .WithName("SaveUserPreferences");
    }
}
