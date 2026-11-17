using FluentValidation;
using PoRepoLineTracker.Shared.Domain;
using PoRepoLineTracker.Shared.Models.Dtos;

namespace PoRepoLineTracker.Shared.Models;

/// <summary>
/// The bulk-add repository rules. <see cref="BulkRepositoryDtoValidator"/> is the one rule set:
/// the bulk endpoint returns its failures as <c>Results.ValidationProblem</c>, and the client checks
/// every entry with it before sending.
/// </summary>
public static class RepositoryValidators
{
    internal static string InvalidName(string field) =>
        $"'{field}' may only contain letters, digits, '-', '_' and '.'.";

    /// <summary>
    /// The characters GitHub allows in an owner or repository name. Both values are interpolated
    /// into the clone URL the server builds, so anything that could change that URL's host or
    /// path ('/', '@', ':', whitespace, a bare "." or "..") is rejected here, at the boundary.
    /// </summary>
    public static bool IsGitHubName(string value) =>
        value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>
/// FluentValidation validator for one bulk-add entry (Rule 11 & Rule 17).
/// </summary>
public sealed class BulkRepositoryDtoValidator : AbstractValidator<BulkRepositoryDto>
{
    public BulkRepositoryDtoValidator()
    {
        RuleFor(x => x.Owner)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("'Owner' must not be empty.")
            .MaximumLength(100).WithMessage("'Owner' must be 100 characters or fewer.")
            .Must(RepositoryValidators.IsGitHubName).WithMessage(RepositoryValidators.InvalidName("Owner"));

        RuleFor(x => x.RepoName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("'RepoName' must not be empty.")
            .MaximumLength(100).WithMessage("'RepoName' must be 100 characters or fewer.")
            .Must(RepositoryValidators.IsGitHubName).WithMessage(RepositoryValidators.InvalidName("RepoName"));
    }
}

/// <summary>
/// Rules for the user-preferences PUT. Mirrors what <c>UserPreferencesEntity</c> can store:
/// extensions are joined with ',' and globs with ';', so a value holding its separator would split
/// in two on the way back, and an unbounded list overflows the table property.
/// </summary>
public sealed class UserPreferencesValidator : AbstractValidator<UserPreferences>
{
    public const int MaxExtensions = 200;
    public const int MaxGlobs = 100;

    public UserPreferencesValidator()
    {
        RuleFor(x => x.FileExtensions)
            .Must(l => l is null || l.Count <= MaxExtensions)
            .WithMessage($"At most {MaxExtensions} extensions can be counted.");
        RuleForEach(x => x.FileExtensions)
            .Must(e => e is { Length: > 0 and <= FileExtensionRules.MaxLength } && !e.Contains(','))
            .WithMessage($"Each extension must be 1 to {FileExtensionRules.MaxLength} characters with no ','.");

        RuleFor(x => x.CustomIgnoreGlobs)
            .Must(l => l is null || l.Count <= MaxGlobs)
            .WithMessage($"At most {MaxGlobs} ignore patterns.");
        RuleForEach(x => x.CustomIgnoreGlobs)
            .Must(g => g is { Length: > 0 and <= 200 } && !g.Contains(';'))
            .WithMessage("Each ignore pattern must be 1 to 200 characters with no ';'.");
    }
}

/// <summary>The extension a user types into Settings (Add dialog or an in-place row edit).</summary>
public sealed class FileExtensionInput
{
    public string Extension { get; set; } = string.Empty;
}

/// <summary>What counts as a file extension, shared by the Settings form and its validator.</summary>
public static class FileExtensionRules
{
    public const int MaxLength = 32;

    /// <summary>Trimmed, lower-case, with a leading dot: "PY " becomes ".py".</summary>
    public static string Normalize(string? typed)
    {
        var ext = (typed ?? string.Empty).Trim().ToLowerInvariant();
        return ext.Length == 0 || ext.StartsWith('.') ? ext : "." + ext;
    }

    public static bool IsValid(string normalized) =>
        normalized.Length is >= 2 and <= MaxLength && normalized[1..].All(char.IsAsciiLetterOrDigit);
}

/// <summary>
/// Validates one typed extension. <paramref name="isDuplicate"/> receives the normalised value
/// and says whether the list already has it (the row being edited excludes itself).
/// </summary>
public sealed class FileExtensionInputValidator : AbstractValidator<FileExtensionInput>
{
    public FileExtensionInputValidator(Func<string, bool>? isDuplicate = null)
    {
        RuleFor(x => x.Extension)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter an extension, for example .py")
            .Must(e => FileExtensionRules.IsValid(FileExtensionRules.Normalize(e)))
            .WithMessage($"Use letters and digits after the dot, up to {FileExtensionRules.MaxLength} characters (for example .py).")
            .Must(e => isDuplicate?.Invoke(FileExtensionRules.Normalize(e)) != true)
            .WithMessage("That extension is already in the list.");
    }
}

/// <summary>The typed confirmation on Settings → Delete account.</summary>
public sealed class DeleteAccountConfirmation
{
    public const string Word = "DELETE";
    public string Confirmation { get; set; } = string.Empty;
}

public sealed class DeleteAccountConfirmationValidator : AbstractValidator<DeleteAccountConfirmation>
{
    public DeleteAccountConfirmationValidator()
    {
        RuleFor(x => x.Confirmation)
            .Equal(DeleteAccountConfirmation.Word, StringComparer.Ordinal)
            .WithMessage($"Type {DeleteAccountConfirmation.Word} in capitals to confirm.");
    }
}
