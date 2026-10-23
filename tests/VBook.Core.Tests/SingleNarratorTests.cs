using VBook.Core.Narrative;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class SingleNarratorTests
{
    private const string Nonfiction =
        "Chapter 1\n\nHabits compound over time. Small gains add up.\n\n“Atomic” is a nice word.\n\nLast paragraph.";

    [Fact]
    public async Task SingleNarrator_SegmentsByParagraphWithoutCastAndCoversText()
    {
        var analyzer = new ChapterAnalyzer(new MockLlmProvider());

        var analysis = await analyzer.AnalyzeAsync(
            new AnalysisRequest("c1", Nonfiction, [], NarrationMode.SingleNarrator), null, default);

        Assert.Equal(4, analysis.Segments.Count);
        Assert.All(analysis.Segments, s => Assert.Equal(SegmentKind.Narration, s.Kind));
        Assert.Empty(analysis.Characters);
        Assert.Equal(Nonfiction, string.Concat(analysis.Segments.Select(s => s.TextFrom(Nonfiction))));
        Assert.True(analysis.Matches(Nonfiction));
    }

    [Fact]
    public void Segmenter_SplitsLongParagraphsAtSentencesAndStaysContiguous()
    {
        var text = string.Concat(Enumerable.Repeat("This is a sentence. ", 100)).Trim() + "\n\nEnd.";

        var segments = SingleNarratorSegmenter.Segment(text, 300);

        Assert.All(segments, s => Assert.InRange(s.Length, 1, 300));
        Assert.Equal(text, string.Concat(segments.Select(s => s.TextFrom(text))));
        Assert.Empty(AnalysisValidator.Validate(text, [], segments));
        Assert.EndsWith(". ", segments[0].TextFrom(text));
    }

    [Fact]
    public void Segmenter_HandlesEmptyText()
    {
        Assert.Empty(SingleNarratorSegmenter.Segment(string.Empty));
    }

    [Fact]
    public void Segmenter_KeepsExplicitLineBreaksAsSegmentBoundaries()
    {
        var verse = "First line.\nSecond line.\n\nThird line.";

        var segments = SingleNarratorSegmenter.Segment(verse);

        Assert.Equal(3, segments.Count);
        Assert.Equal("First line.\n", segments[0].TextFrom(verse));
        Assert.Equal("Second line.\n\n", segments[1].TextFrom(verse));
        Assert.Equal("Third line.", segments[2].TextFrom(verse));
        Assert.Empty(AnalysisValidator.Validate(verse, [], segments));
    }

    [Fact]
    public void Detector_SuggestsSingleNarratorForNonfictionAndMultiVoiceForDialogue()
    {
        Assert.Equal(NarrationMode.SingleNarrator, NarrationModeDetector.Suggest(Nonfiction));

        var fiction = string.Concat(Enumerable.Repeat("“Where are you going?” asked Mary. “Out,” said Tom.\n\n", 20));
        Assert.Equal(NarrationMode.MultiVoice, NarrationModeDetector.Suggest(fiction));
    }
}
