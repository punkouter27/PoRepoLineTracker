using Azure;
using Azure.Data.Tables;
using System.Text.Json;

namespace PoRepoLineTracker.API.Storage;

public class CommitLineCountEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!; // RepositoryId (as string)
    public string RowKey { get; set; } = default!;       // CommitSha
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public DateTime CommitDate { get; set; }
    public int TotalLines { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string LinesByFileTypeJson { get; set; } = string.Empty; // Stored as JSON string

    // Author information
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorEmail { get; set; } = string.Empty;

    // A TagsJson column used to live here, holding CommitTagger's per-commit classification.
    // Azure Table Storage is schemaless, so rows written before its removal keep the property and
    // it is simply ignored on read — no migration, no backfill.

    public CommitLineCount ToDomainModel()
    {
        return new CommitLineCount
        {
            Id = Id,
            RepositoryId = new RepositoryId(RepositoryId),
            CommitSha = CommitSha,
            CommitDate = CommitDate,
            TotalLines = TotalLines,
            LinesAdded = LinesAdded,
            LinesRemoved = LinesRemoved,
            LinesByFileType = string.IsNullOrEmpty(LinesByFileTypeJson)
                ? new Dictionary<string, int>()
                : JsonSerializer.Deserialize(LinesByFileTypeJson, AppJsonSerializerContext.Default.DictionaryStringInt32) ?? new Dictionary<string, int>(),
            AuthorName = AuthorName,
            AuthorEmail = AuthorEmail
        };
    }

    public static CommitLineCountEntity FromDomainModel(CommitLineCount model)
    {
        return new CommitLineCountEntity
        {
            PartitionKey = model.RepositoryId.ToString(),
            RowKey = model.CommitSha,
            Id = model.Id,
            RepositoryId = model.RepositoryId.Value,
            CommitSha = model.CommitSha,
            CommitDate = model.CommitDate.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(model.CommitDate, DateTimeKind.Utc)
                : model.CommitDate.ToUniversalTime(),
            TotalLines = model.TotalLines,
            LinesAdded = model.LinesAdded,
            LinesRemoved = model.LinesRemoved,
            LinesByFileTypeJson = JsonSerializer.Serialize(model.LinesByFileType, AppJsonSerializerContext.Default.DictionaryStringInt32),
            AuthorName = model.AuthorName,
            AuthorEmail = model.AuthorEmail
        };
    }
}
