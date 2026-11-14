using VBook.Core.Narrative;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class ChapterAnalyzerTests
{
    [Fact]
    public async Task ChapterAnalyzer_CombinesSegmentationAndSourceAnchoredSpeechEdits()
    {
        const string source = "Read this paragraph.\n\nVisit https://example.test now.";
        var edit = new SpeechEditProposal(
            "https://example.test",
            "the example website",
            "A raw URL is awkward to hear aloud.");
        var analyzer = new ChapterAnalyzer(new FakeProvider([edit]));

        var analysis = await analyzer.AnalyzeAsync(
            new AnalysisRequest("chapter-1", source, [], NarrationMode.MultiVoice),
            null,
            CancellationToken.None);

        var suggestion = Assert.Single(analysis.SpeechSuggestions!);
        Assert.Equal(source.IndexOf("https://example.test", StringComparison.Ordinal), suggestion.Start);
        Assert.Equal(edit.OriginalText, source.Substring(suggestion.Start, suggestion.Length));
        Assert.Equal(SpeechEditStatus.Pending, suggestion.Status);
        AnalysisValidator.EnsureValid(source, [], analysis.Segments);
    }

    [Fact]
    public async Task ChapterAnalyzer_IgnoresSuggestionsThatCrossDialogueBoundariesAndReportsWarning()
    {
        const string source = "Narration. “Dialogue.”";
        var dialogueStart = source.IndexOf('“');
        var provider = new FakeProvider(
            [new SpeechEditProposal("“Dialogue.”", "a spoken phrase", "Normalize the dialogue.")],
            [
                new NarrativeSegment(0, SegmentKind.Narration, null, 0, dialogueStart),
                new NarrativeSegment(
                    1,
                    SegmentKind.Dialogue,
                    "ada",
                    dialogueStart,
                    source.Length - dialogueStart)
            ],
            [new Character("ada", "Ada", [])]);

        var analysis = await new ChapterAnalyzer(provider).AnalyzeAsync(
            new AnalysisRequest("chapter-1", source, [], NarrationMode.MultiVoice),
            null,
            CancellationToken.None);

        Assert.Empty(analysis.SpeechSuggestions!);
        Assert.NotEmpty(analysis.Warnings!);
        AnalysisValidator.EnsureValid(source, ["ada"], analysis.Segments);
    }

    [Fact]
    public void SpeechScriptBuilder_AppliesAcceptedSuggestionsOnly()
    {
        const string source = "Start. A citation. End.";
        var start = source.IndexOf("A citation.", StringComparison.Ordinal);
        var review = new SpeechEditReview(
            "chapter-1",
            TextFingerprint.Compute(source),
            [
                new SpeechEditSuggestion(
                    Guid.NewGuid(), start, "A citation.".Length, "A citation.", "the citation", "Shorten it.",
                    SpeechEditStatus.Accepted),
                new SpeechEditSuggestion(
                    Guid.NewGuid(), 0, "Start.".Length, "Start.", "Opening.", "Alternative wording.")
            ]);

        Assert.Equal("Start. the citation End.", SpeechScriptBuilder.Apply(source, review));
    }

    [Fact]
    public void SpeechScriptBuilder_IgnoresReviewsForChangedSource()
    {
        const string source = "Original text.";
        var review = new SpeechEditReview(
            "chapter-1",
            TextFingerprint.Compute(source),
            [
                new SpeechEditSuggestion(
                    Guid.NewGuid(), 0, source.Length, source, "Changed text.", "Suggested change.",
                    SpeechEditStatus.Accepted)
            ]);

        Assert.Equal(source + " edited", SpeechScriptBuilder.Apply(source + " edited", review));
    }

    [Fact]
    public void SpeechScriptBuilder_RejectsOverlappingAcceptedEdits()
    {
        const string source = "one two three";
        var review = new SpeechEditReview(
            "chapter-1",
            TextFingerprint.Compute(source),
            [
                new SpeechEditSuggestion(Guid.NewGuid(), 0, 7, "one two", "first", "Reason.", SpeechEditStatus.Accepted),
                new SpeechEditSuggestion(Guid.NewGuid(), 4, 9, "two three", "second", "Reason.", SpeechEditStatus.Accepted)
            ]);

        Assert.Throws<InvalidDataException>(() => SpeechScriptBuilder.Apply(source, review));
    }

    private sealed class FakeProvider(
        IReadOnlyList<SpeechEditProposal> edits,
        IReadOnlyList<NarrativeSegment>? segments = null,
        IReadOnlyList<Character>? characters = null) : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("fake", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SegmentationResult(
                characters ?? [],
                segments ?? [new NarrativeSegment(0, SegmentKind.Narration, null, 0, request.Text.Length)],
                edits));
    }
}
