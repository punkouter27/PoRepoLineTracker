namespace PoRepoLineTracker.Api.Features.Repositories;

public record GetAllRepositoriesQuery(UserId UserId);

public class GetAllRepositoriesQueryHandler
{
    private readonly IRepositoryDataService _repositoryDataService;

    public GetAllRepositoriesQueryHandler(IRepositoryDataService repositoryDataService)
        => _repositoryDataService = repositoryDataService;

    public Task<IEnumerable<GitHubRepository>> Handle(GetAllRepositoriesQuery request, CancellationToken cancellationToken = default)
        => _repositoryDataService.GetAllRepositoriesAsync(request.UserId);
}
