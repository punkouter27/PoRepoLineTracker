using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace PoRepoLineTracker.Api.Features.Repositories;

public record GetFileExtensionPercentagesQuery(RepositoryId RepositoryId);

public class GetFileExtensionPercentagesQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;

    public GetFileExtensionPercentagesQueryHandler(IRepositoryDataService repositoryDataService)
    {
        _repositoryDataService = repositoryDataService;
    }

    public async Task<IEnumerable<FileExtensionPercentageDto>> Handle(GetFileExtensionPercentagesQuery request, CancellationToken cancellationToken = default)
    {
        // Get commit line counts for the repository
        var commitLineCounts = await _repositoryDataService.GetCommitLineCountsByRepositoryIdAsync(request.RepositoryId);

        if (!commitLineCounts.Any())
        {
            return Enumerable.Empty<FileExtensionPercentageDto>();
        }

        // Aggregate line counts by file extension
        var extensionLineCounts = new Dictionary<string, int>();
        int totalLines = 0;

        foreach (var commit in commitLineCounts)
        {
            foreach (var kvp in commit.LinesByFileType)
            {
                var extension = kvp.Key;
                var lineCount = kvp.Value;

                if (extensionLineCounts.ContainsKey(extension))
                {
                    extensionLineCounts[extension] += lineCount;
                }
                else
                {
                    extensionLineCounts[extension] = lineCount;
                }
                totalLines += lineCount;
            }
        }

        // Calculate percentages
        var result = extensionLineCounts
            .Where(kvp => kvp.Value > 0) // Only include extensions with actual lines
            .Select(kvp => new FileExtensionPercentageDto
            {
                FileExtension = kvp.Key,
                LineCount = kvp.Value,
                Percentage = totalLines > 0 ? (double)kvp.Value / totalLines * 100 : 0
            })
            .OrderByDescending(dto => dto.LineCount) // Sort by line count descending
            .ToList();

        return result;
    }
}
