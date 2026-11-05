using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace PoRepoLineTracker.Unit;

/// <summary>
/// Unit tests for <see cref="FileIgnoreFilter"/>.
/// One or two representative rows per rule family; the embedded-repository-root (content-based)
/// rule keeps richer coverage because it is the load-bearing one — see that region.
/// </summary>
public class FileIgnoreFilterTests
{
    private readonly FileIgnoreFilter _filter;

    public FileIgnoreFilterTests()
    {
        var logger = Substitute.For<ILogger<FileIgnoreFilter>>();
        _filter = new FileIgnoreFilter(logger);
    }

    [Fact]
    public void ShouldIgnoreFile_PrunesThirdPartyGeneratedAndMigrations_KeepsSource()
    {
        _filter.ShouldIgnoreFile("packages.config", "").Should().BeTrue();
        _filter.ShouldIgnoreFile("AssemblyInfo.cs", "").Should().BeTrue();
        _filter.ShouldIgnoreFile("20231001_Init.cs", "src/Migrations/20231001_Init.cs").Should().BeTrue();

        _filter.ShouldIgnoreFile("Program.cs", "").Should().BeFalse();
        _filter.ShouldIgnoreFile("package.json", "").Should().BeFalse();
        _filter.ShouldIgnoreFile("MyService.cs", "src/Services/MyService.cs").Should().BeFalse();
    }

    [Fact]
    public void ShouldIgnoreDirectory_PrunesVendoredAndGenerated_KeepsSource()
    {
        _filter.ShouldIgnoreDirectory("src/bin").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Packages/com.unity.ml-agents").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("NODE_MODULES").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Library").Should().BeTrue();

        _filter.ShouldIgnoreDirectory("src").Should().BeFalse();
        // Only a root-level Library is Unity's cache; a nested one is source.
        _filter.ShouldIgnoreDirectory("src/Library").Should().BeFalse();
    }

    /// <summary>The user's own ignore globs: matched on their own, then applied to files and directories.</summary>
    /// <summary>
    /// An Unreal project: its C++ counts, with C-style comments, while the editor's generated and
    /// vendored trees (at the project root or a plugin's) stay out of the totals.
    /// </summary>
    [Fact]
    public void UnrealProject_CountsCpp_SkipsGeneratedAndEngineFolders()
    {
        UserPreferences.DefaultFileExtensions.Should().Contain([".cpp", ".h", ".hpp", ".cc", ".cxx", ".inl", ".c"]);
        CommentSyntax.For(".cpp").Should().Be(CommentSyntax.For(".cs"));
        CommentSyntax.For(".h").Should().Be(CommentSyntax.For(".cs"));

        _filter.ShouldIgnoreFile("MyActor.generated.h", "Source/MyGame/MyActor.generated.h").Should().BeTrue();
        _filter.ShouldIgnoreFile("MyActor.h", "Source/MyGame/MyActor.h").Should().BeFalse();

        _filter.ShouldIgnoreDirectory("Intermediate").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Plugins/MyPlugin/Binaries").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("DerivedDataCache").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Saved").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Source/ThirdParty").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("Plugins/MyPlugin/Source/ThirdParty").Should().BeTrue();

        _filter.ShouldIgnoreDirectory("Source/MyGame").Should().BeFalse();
        _filter.ShouldIgnoreDirectory("Source/MyGame/Saved").Should().BeFalse(); // Saved/ only at the root
    }

    [Fact]
    public void CustomGlobs_MatchAndPruneFilesAndDirectories()
    {
        FileIgnoreFilter.MatchesCustomGlobs("src/Generated/Models.cs", ["src/Generated/**"]).Should().BeTrue();
        FileIgnoreFilter.MatchesCustomGlobs("src/Data/UserDbContextModelSnapshot.g.cs", ["**/*.g.cs"]).Should().BeTrue();
        FileIgnoreFilter.MatchesCustomGlobs("docs/README.md", ["docs/**"]).Should().BeTrue();
        FileIgnoreFilter.MatchesCustomGlobs("src/Controllers/HomeController.cs", ["**/*.g.cs"]).Should().BeFalse();
        FileIgnoreFilter.MatchesCustomGlobs("src/Services/PaymentService.cs", ["docs/**"]).Should().BeFalse();

        _filter.CustomIgnoreGlobs = ["**/fixtures/**", "**/*.fixture.json", "**/testdata/**"];
        _filter.ShouldIgnoreFile("user.fixture.json", "tests/fixtures/user.fixture.json").Should().BeTrue();
        _filter.ShouldIgnoreFile("sample.txt", "tests/fixtures/sample.txt").Should().BeTrue();
        _filter.ShouldIgnoreFile("UserService.cs", "src/Services/UserService.cs").Should().BeFalse();
        _filter.ShouldIgnoreDirectory("tests/testdata").Should().BeTrue();
        _filter.ShouldIgnoreDirectory("src/Services").Should().BeFalse();
    }

    [Fact]
    public void ShouldIgnoreDirectory_EmbeddedRootsRules_AreAccurate()
    {
        string[] rootEntries =
        [
            ".github", ".gitmodules", ".gitattributes", ".pre-commit-config.yaml",
            "CODEOWNERS", "CODE_OF_CONDUCT.md", "LICENSE.md", "Readme.md"
        ];

        _filter.ShouldIgnoreDirectory("Training/ml-agents", rootEntries).Should().BeTrue();
        _filter.ShouldIgnoreDirectory("", rootEntries).Should().BeFalse();
        _filter.ShouldIgnoreDirectory("src/Feature", ["LICENSE.md", "Handler.cs"]).Should().BeFalse();
        _filter.ShouldIgnoreDirectory("app/node_modules", ["index.js"]).Should().BeTrue();
    }

    [Fact]
    public void StripCredentials_RemovesUserInfo_LeavesUnparseableAlone()
    {
        GitClient.StripCredentials("https://x-access-token:ghp_secret@github.com/o/r.git")
            .Should().Be("https://github.com/o/r.git");
        GitClient.StripCredentials("https://github.com/o/r.git")
            .Should().Be("https://github.com/o/r.git");
        GitClient.StripCredentials("")
            .Should().Be("");
        GitClient.StripCredentials("git@github.com:o/r.git")
            .Should().Be("git@github.com:o/r.git");
    }
}
