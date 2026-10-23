using VBook.Core.Narrative;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class ChapterAnalyzerTests
{
    private const string Sample =
        "Mary looked up.\n\n“Where have you been?” asked Mary.\n\nTom shrugged. \"Out,\" he said.\n\n" +
        "Tom said, \"Nowhere special.\" The rain kept falling.";

    [Fact]
    public async Task MockProvider_CoversSourceExactlyAndAttributesSpeakers()
    {
        var analyzer = new ChapterAnalyzer(new MockLlmProvider());

        var analysis = await analyzer.AnalyzeAsync(new AnalysisRequest("c1", Sample, []), null, default);

        Assert.Equal(Sample, string.Concat(analysis.Segments.Select(segment => segment.TextFrom(Sample))));
        var dialogue = analysis.Segments.Where(segment => segment.Kind == SegmentKind.Dialogue).ToArray();
        Assert.Equal(["mary", "unknown", "tom"], dialogue.Select(segment => segment.SpeakerId));
        Assert.Contains(analysis.Characters, character => character.Name == "Mary");
        Assert.True(analysis.Matches(Sample));
        Assert.False(analysis.Matches(Sample + "!"));
    }

    [Fact]
    public async Task KnownCharacters_AreReusedByNameOrAlias()
    {
        var known = new[] { new Character("mary-jones", "Mary Jones", ["Mary"]) };
        var analyzer = new ChapterAnalyzer(new MockLlmProvider());

        var analysis = await analyzer.AnalyzeAsync(new AnalysisRequest("c1", Sample, known), null, default);

        Assert.Equal("mary-jones", analysis.Segments.First(s => s.Kind == SegmentKind.Dialogue).SpeakerId);
        Assert.DoesNotContain(analysis.Characters, character => character.Id == "mary");
    }

    [Fact]
    public async Task EmptyText_ProducesNoSegments()
    {
        var analyzer = new ChapterAnalyzer(new MockLlmProvider());

        var analysis = await analyzer.AnalyzeAsync(new AnalysisRequest("c1", "", []), null, default);

        Assert.Empty(analysis.Segments);
    }

    [Fact]
    public async Task LongText_IsChunkedToBudget_AndOffsetsStayExact()
    {
        var provider = new RecordingProvider(maxContext: 120);
        var analyzer = new ChapterAnalyzer(provider);
        var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"“Line {i},” said Ann. Narration {i}."));
        var reports = new List<AnalysisProgress>();

        var analysis = await analyzer.AnalyzeAsync(
            new AnalysisRequest("c1", text, []), new ListProgress(reports), default);

        Assert.True(provider.Requests.Count > 1);
        Assert.All(provider.Requests, request => Assert.True(request.Text.Length <= 120));
        Assert.Equal(text, string.Concat(analysis.Segments.Select(segment => segment.TextFrom(text))));
        Assert.Equal(Enumerable.Range(0, analysis.Segments.Count), analysis.Segments.Select(s => s.Index));
        Assert.Contains(provider.Requests.Skip(1), request => request.KnownCharacters.Any(c => c.Id == "ann"));
        Assert.Equal(1, reports[^1].Fraction);
    }

    [Fact]
    public async Task ProviderOutputWithGap_IsRejected()
    {
        var analyzer = new ChapterAnalyzer(new GappyProvider());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => analyzer.AnalyzeAsync(new AnalysisRequest("c1", "Some text here.", []), null, default));
    }

    [Fact]
    public async Task Cancellation_StopsAnalysis()
    {
        var analyzer = new ChapterAnalyzer(new MockLlmProvider());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(new AnalysisRequest("c1", Sample, []), null, cancellation.Token));
    }

    [Theory]
    [InlineData("a b c d e f g h i j", 6)]
    [InlineData("no-spaces-at-all-in-this-text", 5)]
    [InlineData("line one\nline two\nline three\n", 12)]
    public void Chunker_ProducesContiguousBoundedChunks(string text, int max)
    {
        var chunks = TextChunker.Split(text, max);

        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= max));
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        var expectedStart = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(expectedStart, chunk.Start);
            expectedStart += chunk.Text.Length;
        }
    }

    [Fact]
    public void Validator_ReportsOverlapsAndMissingSpeakers()
    {
        var errors = AnalysisValidator.Validate(
            "abcdef",
            ["a"],
            [
                new NarrativeSegment(0, SegmentKind.Dialogue, "zzz", 0, 4),
                new NarrativeSegment(1, SegmentKind.Narration, null, 3, 3),
            ]);

        Assert.Contains(errors, error => error.Contains("unknown speaker"));
        Assert.Contains(errors, error => error.Contains("gap or overlap"));
    }

    private sealed class ListProgress(List<AnalysisProgress> reports) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => reports.Add(value);
    }

    private sealed class RecordingProvider(int maxContext) : ILlmProvider
    {
        private readonly MockLlmProvider _inner = new();

        public List<AnalysisRequest> Requests { get; } = [];

        public ProviderCapabilities Capabilities { get; } = new("recording", true, maxContext);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _inner.AnalyzeAsync(request, cancellationToken);
        }
    }

    private sealed class GappyProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("gappy", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SegmentationResult(
                [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, 4)]));
    }
}
