// Cross-cutting namespaces every slice may use. Deliberately excludes
// PoRepoLineTracker.Api.Features.* — slices must not reference each other, so a slice
// that needs another slice's type has to import it explicitly and justify the coupling.
// Only the composition root (Extensions/) does that.
global using PoRepoLineTracker.Api.Analysis;
global using PoRepoLineTracker.Api.Auth;
global using PoRepoLineTracker.Api.Extensions;
global using PoRepoLineTracker.Api.Middleware;
global using PoRepoLineTracker.Api.Storage;
global using PoRepoLineTracker.Api.Telemetry;
global using PoRepoLineTracker.Shared.Domain;
global using PoRepoLineTracker.Shared.Models;
global using PoRepoLineTracker.Shared.Models.Dtos;
global using PoRepoLineTracker.Shared.Serialization;
