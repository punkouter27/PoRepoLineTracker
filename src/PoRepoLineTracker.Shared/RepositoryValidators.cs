using FluentValidation;
using PoRepoLineTracker.Shared.Models.Dtos;

namespace PoRepoLineTracker.Shared.Models;

/// <summary>
/// The bulk-add repository rules. Returns null when valid, otherwise field errors for
/// <see cref="Microsoft.AspNetCore.Http.Results.ValidationProblem"/>, and exposes a FluentValidation
/// <see cref="BulkRepositoryDtoValidator"/> for forms and minimal endpoints.
/// </summary>
public static class RepositoryValidators
{
    private const int MaxLength = 100;

    public static Dictionary<string, string[]>? Validate(BulkRepositoryDto dto)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(dto.Owner))
            errors[nameof(dto.Owner)] = ["'Owner' must not be empty."];
        else if (dto.Owner.Length > MaxLength)
            errors[nameof(dto.Owner)] = [$"'Owner' must be {MaxLength} characters or fewer."];
        else if (!IsGitHubName(dto.Owner))
            errors[nameof(dto.Owner)] = [InvalidName("Owner")];

        if (string.IsNullOrWhiteSpace(dto.RepoName))
            errors[nameof(dto.RepoName)] = ["'RepoName' must not be empty."];
        else if (dto.RepoName.Length > MaxLength)
            errors[nameof(dto.RepoName)] = [$"'RepoName' must be {MaxLength} characters or fewer."];
        else if (!IsGitHubName(dto.RepoName))
            errors[nameof(dto.RepoName)] = [InvalidName("RepoName")];

        return errors.Count == 0 ? null : errors;
    }

    private static string InvalidName(string field) =>
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
/// FluentValidation validator wrapping <see cref="RepositoryValidators"/> (Rule 11 & Rule 17).
/// </summary>
public sealed class BulkRepositoryDtoValidator : AbstractValidator<BulkRepositoryDto>
{
    public BulkRepositoryDtoValidator()
    {
        RuleFor(x => x.Owner)
            .NotEmpty().WithMessage("'Owner' must not be empty.")
            .MaximumLength(100).WithMessage("'Owner' must be 100 characters or fewer.")
            .Must(RepositoryValidators.IsGitHubName).WithMessage("'Owner' may only contain letters, digits, '-', '_' and '.'.");

        RuleFor(x => x.RepoName)
            .NotEmpty().WithMessage("'RepoName' must not be empty.")
            .MaximumLength(100).WithMessage("'RepoName' must be 100 characters or fewer.")
            .Must(RepositoryValidators.IsGitHubName).WithMessage("'RepoName' may only contain letters, digits, '-', '_' and '.'.");
    }
}
