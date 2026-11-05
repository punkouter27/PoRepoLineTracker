using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PoRepoLineTracker.API.Analysis;
using PoRepoLineTracker.API.Features.Repositories;

namespace PoRepoLineTracker.API;

/// <summary>
/// Source-generated JSON for the server's own payloads: GitHub API bodies and the error
/// middleware's problem details. Wire types shared with the client are in
/// <c>AppJsonSerializerContext</c>. Generated rather than reflection-based, so the trim/AOT
/// analyzers the API builds with stay clean.
/// </summary>
[JsonSerializable(typeof(List<GitHubApiRepository>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(GitHubHookRequest))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
