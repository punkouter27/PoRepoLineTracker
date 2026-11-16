namespace PoRepoLineTracker.Api.Features.Repositories;

public record GetLineCountHistoryQuery(RepositoryId RepositoryId, int Days);

public class GetLineCountHistoryQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;

    public GetLineCountHistoryQueryHandler(IRepositoryDataService repositoryDataService)
        => _repositoryDataService = repositoryDataService;

    public Task<IEnumerable<DailyLineCountDto>> Handle(GetLineCountHistoryQuery request, CancellationToken cancellationToken = default)
        => _repositoryDataService.GetLineCountHistoryAsync(request.RepositoryId, request.Days);
}
