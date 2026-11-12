using FluentAssertions;
using PoRepoLineTracker.Api.Features.Repositories;
using PoRepoLineTracker.Shared.Models.Dtos;
using PoRepoLineTracker.Shared.Serialization;
using System.Text.Json;

namespace PoRepoLineTracker.Unit.Features.Repositories;

public class PortfolioExportTests
{
    [Fact]
    public void BuildCsv_generates_valid_csv_header_and_records()
    {
        var rows = new List<PortfolioExportRowDto>
        {
            new()
            {
                Owner = "alice",
                Name = "frontend-app",
                TotalLines = 14200,
                LastAnalyzedUtc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
                CloneUrl = "https://github.com/alice/frontend-app.git"
            },
            new()
            {
                Owner = "bob",
                Name = "api,with,commas",
                TotalLines = null,
                LastAnalyzedUtc = null,
                CloneUrl = "https://github.com/bob/api.git"
            }
        };

        var csv = PortfolioExportEndpoints.BuildCsv(rows);

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines[0].Should().Be("Owner,Repository,TotalLines,LastAnalyzedUtc,CloneUrl");
        lines[1].Should().Be("alice,frontend-app,14200,2026-09-20T12:00:00.0000000Z,https://github.com/alice/frontend-app.git");
        lines[2].Should().Be("bob,\"api,with,commas\",,,https://github.com/bob/api.git");
    }

    [Fact]
    public void EscapeCsv_handles_special_characters()
    {
        PortfolioExportEndpoints.EscapeCsv("simple").Should().Be("simple");
        PortfolioExportEndpoints.EscapeCsv("has,comma").Should().Be("\"has,comma\"");
        PortfolioExportEndpoints.EscapeCsv("has\"quote").Should().Be("\"has\"\"quote\"");
        PortfolioExportEndpoints.EscapeCsv("has\nnewline").Should().Be("\"has\nnewline\"");
    }

    [Fact]
    public void Serializes_and_deserializes_PortfolioExportRowDto_with_trim_safe_context()
    {
        var original = new List<PortfolioExportRowDto>
        {
            new() { Owner = "punkouter26", Name = "PoRepoLineTracker", TotalLines = 25000 }
        };

        var json = JsonSerializer.Serialize(original, AppJsonSerializerContext.Default.ListPortfolioExportRowDto);
        var deserialized = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ListPortfolioExportRowDto);

        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(1);
        deserialized[0].Owner.Should().Be("punkouter26");
        deserialized[0].Name.Should().Be("PoRepoLineTracker");
        deserialized[0].TotalLines.Should().Be(25000);
    }
}

