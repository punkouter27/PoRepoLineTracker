using FluentAssertions;
namespace PoRepoLineTracker.Unit;

/// <summary>
/// The judging half of code health. Scoring is all judgement calls, so what is worth pinning is
/// not the exact numbers but the PROPERTIES a reader will assume hold: that every factor points
/// the same way, that the composite honours its weights, that worse input never scores better, and
/// that the hotspot list surfaces real offenders rather than trivia.
/// </summary>
public class CodeHealthScoringTests
{
    private static FileMetrics File(
        string path,
        int codeLines,
        int complexity = 1,
        int nesting = 2,
        int comments = 0,
        int longLines = 0,
        int debt = 0) =>
        new(path, Path.GetExtension(path), codeLines, comments, 0, complexity, nesting, longLines, debt);

    [Fact]
    public void Build_HandlesEmptyAndZeroCodeLines()
    {
        var empty = CodeHealthScoring.Build([]);
        empty.HasData.Should().BeFalse();
        empty.Factors.Should().BeEmpty();

        var zeroCode = CodeHealthScoring.Build([File("a.cs", codeLines: 0, comments: 20)]);
        zeroCode.HasData.Should().BeFalse();
    }

    [Fact]
    public void Build_CalculatesFactorScores_AndHonorsWeightsAndGradingBands()
    {
        var report = CodeHealthScoring.Build([File("a.cs", codeLines: 200, complexity: 30, comments: 40)]);
        report.Factors.Should().HaveCount(6);
        report.Factors.Should().OnlyContain(f => f.Score >= 0 && f.Score <= 100);
        report.Factors.Sum(f => f.Weight).Should().Be(100);
        report.Factors.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Measurement));
        report.Factors.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Explanation));

        var clean = CodeHealthScoring.Build(Enumerable.Range(0, 10)
            .Select(i => File($"src/File{i}.cs", codeLines: 120, complexity: 12, nesting: 2, comments: 25))
            .ToList());
        clean.Score.Should().BeGreaterThan(80);
        clean.Grade.Should().BeOneOf("A", "B");

        var tangled = CodeHealthScoring.Build(Enumerable.Range(0, 10)
            .Select(i => File($"src/File{i}.cs", codeLines: 900, complexity: 400, nesting: 9, comments: 5, longLines: 300, debt: 20))
            .ToList());
        tangled.Score.Should().BeLessThan(45);
        tangled.Grade.Should().Be("F");
    }

    [Fact]
    public void Build_EnforcesMonotonicityAndSizeRules()
    {
        var simple = CodeHealthScoring.Build([File("a.cs", codeLines: 300, complexity: 20)]);
        var tangled = CodeHealthScoring.Build([File("a.cs", codeLines: 300, complexity: 120)]);
        tangled.Score.Should().BeLessThan(simple.Score);

        var shallow = CodeHealthScoring.Build([File("a.cs", codeLines: 300, nesting: 2)]);
        var deep = CodeHealthScoring.Build([File("a.cs", codeLines: 300, nesting: 10)]);
        deep.Score.Should().BeLessThan(shallow.Score);

        int DocScore(int comments) => CodeHealthScoring
            .Build([File("a.cs", codeLines: 100, comments: comments)])
            .Factors.Single(f => f.Key == "documentation").Score;
        var healthy = DocScore(25);
        DocScore(0).Should().BeLessThan(healthy);
        DocScore(400).Should().BeLessThan(healthy);

        var withGiant = CodeHealthScoring.Build(
        [
            File("giant.cs", codeLines: 5_000),
            .. Enumerable.Range(0, 20).Select(i => File($"small{i}.cs", codeLines: 50))
        ]);
        var allSmall = CodeHealthScoring.Build(
            Enumerable.Range(0, 20).Select(i => File($"small{i}.cs", codeLines: 50)).ToList());
        withGiant.Factors.Single(f => f.Key == "file-size").Score
            .Should().BeLessThan(allSmall.Factors.Single(f => f.Key == "file-size").Score);
    }

    [Fact]
    public void Build_SurfacesHotspotsAndLanguageBreakdowns()
    {
        var report = CodeHealthScoring.Build(
        [
            File("src/Fine.cs", codeLines: 100, complexity: 8, nesting: 2),
            File("src/Awful.cs", codeLines: 1_500, complexity: 500, nesting: 11, debt: 15),
            File("src/Middling.cs", codeLines: 300, complexity: 60, nesting: 5),
            File("src/Tiny.cs", codeLines: 4, complexity: 4, nesting: 3)
        ]);

        report.Hotspots.First().Path.Should().Be("src/Awful.cs");
        report.Hotspots.First().PrimaryConcern.Should().NotBeNullOrWhiteSpace();
        report.Hotspots.Should().NotContain(h => h.Path == "src/Tiny.cs");

        var byLang = CodeHealthScoring.Build(
        [
            File("a.cs", codeLines: 1_000, complexity: 300, nesting: 9),
            File("b.ts", codeLines: 200, complexity: 20, nesting: 2, comments: 40)
        ]);
        byLang.ByLanguage.Should().HaveCount(2);
        byLang.ByLanguage.First().Extension.Should().Be(".cs");
        var cs = byLang.ByLanguage.Single(l => l.Extension == ".cs");
        var ts = byLang.ByLanguage.Single(l => l.Extension == ".ts");
        ts.Score.Should().BeGreaterThan(cs.Score);
    }
}

/// <summary>
/// The single combined figure. It exists because the two analysers were being shown side by side
/// and visibly disagreed — one repository read "maintainability 62" next to grade A while another
/// read "67" next to grade F, and nothing can be ranked on two scales at once.
/// </summary>
public class CodeHealthScoringCombineTests
{
    [Fact]
    public void Combine_PureLanguagesWeightedDominanceAndRange()
    {
        var (csScore, csGrade) = CodeHealthScoring.Combine(
            maintainabilityIndex: 82, csharpLines: 9_000, heuristicScore: 0, otherLines: 0);
        csScore.Should().Be(82);
        csGrade.Should().Be("B");

        var (nonCsScore, nonCsGrade) = CodeHealthScoring.Combine(
            maintainabilityIndex: null, csharpLines: 0, heuristicScore: 74, otherLines: 5_000);
        nonCsScore.Should().Be(74);
        nonCsGrade.Should().Be("C");

        var (domScore, _) = CodeHealthScoring.Combine(
            maintainabilityIndex: 90, csharpLines: 50_000, heuristicScore: 10, otherLines: 500);
        domScore.Should().BeGreaterThan(85);

        var (equalScore, _) = CodeHealthScoring.Combine(
            maintainabilityIndex: 80, csharpLines: 1_000, heuristicScore: 60, otherLines: 1_000);
        equalScore.Should().Be(70);

        var (emptyScore, emptyGrade) = CodeHealthScoring.Combine(
            maintainabilityIndex: null, csharpLines: 0, heuristicScore: 0, otherLines: 0);
        emptyScore.Should().Be(0);
        emptyGrade.Should().BeEmpty();

        var (zeroLinesScore, _) = CodeHealthScoring.Combine(
            maintainabilityIndex: 40, csharpLines: 0, heuristicScore: 100, otherLines: 0);
        zeroLinesScore.Should().Be(40);

        foreach (var mi in new[] { 0, 50, 100 })
            foreach (var heuristic in new[] { 0, 50, 100 })
            {
                var (score, _) = CodeHealthScoring.Combine(mi, 1_000, heuristic, 1_000);
                score.Should().BeInRange(0, 100);
            }
    }
}
