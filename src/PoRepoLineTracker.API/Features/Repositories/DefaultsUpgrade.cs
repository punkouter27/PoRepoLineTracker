namespace PoRepoLineTracker.Api.Features.Repositories;

/// <summary>
/// One-time upgrade, run at startup, for file types added to the defaults after users already
/// saved their own list. Version 1 (owner, 2026-11-05): C and C++, for Unreal projects.
///
/// <para>For every user who tracks repositories: the new types are added to their saved list
/// once (a stored version marker, so a type they later remove is not added back), then every
/// repository whose clone holds files of those types gets a full re-analysis, because its stored
/// history was counted without them. Repositories with no clone on disk are left alone: their
/// first analysis will use the new list anyway.</para>
///
/// <para>Best-effort and idempotent: a failure is logged and the next start tries again, since
/// the marker is only written for users whose list was upgraded.</para>
/// </summary>
internal static class DefaultsUpgrade
{
    internal const int Version = 1;

    internal static readonly string[] AddedExtensions = [".cpp", ".h", ".hpp", ".cc", ".cxx", ".inl", ".c"];

    internal static async Task RunAsync(IServiceProvider services, AnalysisQueue queue, ILogger logger)
    {
        var data = services.GetRequiredService<IRepositoryDataService>();
        var preferences = services.GetRequiredService<IUserPreferencesService>();
        var gitHub = services.GetRequiredService<IGitHubService>();
        var git = services.GetRequiredService<GitClient>();

        var upgradedUsers = 0;
        var queued = 0;
        foreach (var userRepositories in (await data.GetEveryRepositoryAsync()).GroupBy(r => r.UserId))
        {
            if (!await preferences.UpgradeDefaultsAsync(userRepositories.Key, Version, AddedExtensions))
                continue;
            upgradedUsers++;

            foreach (var repository in userRepositories)
            {
                var path = gitHub.ResolveRepositoryPath(GitHubRepository.CloneDirectoryName(repository.Id));
                if (!GitObjectReader.IsRepository(path)) continue;

                var hasNewTypes = git.TrackedFiles(path)
                    .Any(file => AddedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                if (hasNewTypes && await queue.EnqueueReanalysisAsync(repository, data))
                    queued++;
            }
        }

        if (upgradedUsers > 0)
            logger.LogInformation("File-type defaults v{Version}: upgraded {Users} users' lists, queued {Queued} re-analyses",
                Version, upgradedUsers, queued);
    }
}
