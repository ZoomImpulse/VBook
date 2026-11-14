using System.Net;
using System.Text;
using System.Text.Json;
using VBook.Core.Books;
using VBook.Core.Narrative;
using VBook.Core.Providers;
using Xunit;

namespace VBook.Core.Tests;

public sealed class OpenAiCompatibleLlmProviderTests
{
    [Fact]
    public async Task AnalyzeAsync_MapsExactDialogueAndPreservesAllSourceText()
    {
        const string source = "Ada said “Hi.”\n\nThen she left.";
        var provider = CreateProvider(
            """
            {"characters":[{"name":"Ada","aliases":[]}],"dialogue":[{"text":"“Hi.”","speaker":"Ada"}],"edits":[]}
            """);

        var result = await provider.AnalyzeAsync(
            new AnalysisRequest("c1", source, [], NarrationMode.MultiVoice, SectionRole.FrontMatter),
            CancellationToken.None);
        var character = Assert.Single(result.Characters);

        Assert.Equal("Ada", character.Name);
        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("Ada said ", result.Segments[0].TextFrom(source));
        Assert.Equal("“Hi.”", result.Segments[1].TextFrom(source));
        Assert.Equal("\n\nThen she left.", result.Segments[2].TextFrom(source));
        AnalysisValidator.EnsureValid(source, [character.Id], result.Segments);
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoresDialogueThatDoesNotMatchSourceAndReportsWarning()
    {
        var provider = CreateProvider(
            """
            {"characters":[],"dialogue":[{"text":"“Made up.”","speaker":"Unknown"}],"edits":[]}
            """);

        var result = await provider.AnalyzeAsync(
            new AnalysisRequest("c1", "Source only.", [], NarrationMode.MultiVoice),
            CancellationToken.None);

        Assert.DoesNotContain(result.Segments, segment => segment.Kind == SegmentKind.Dialogue);
        Assert.NotEmpty(result.Warnings!);
        AnalysisValidator.EnsureValid("Source only.", [], result.Segments);
    }

    [Fact]
    public async Task AnalyzeStructureAsync_ReturnsSectionSuggestions()
    {
        var provider = CreateProvider(
            """
            {"genre":"historical nonfiction","chapters":[{"id":"preface","role":"FrontMatter","confidence":0.91,"rationale":"The title identifies this as a preface."}]}
            """);

        var result = await provider.AnalyzeAsync(
            new BookStructureRequest(
                "A Book",
                [new BookStructureChapter("preface", "Preface", "Opening note.", SectionRole.Body)]),
            CancellationToken.None);

        Assert.Equal("historical nonfiction", result.Genre);
        var suggestion = Assert.Single(result.Chapters);
        Assert.Equal("preface", suggestion.ChapterId);
        Assert.Equal(SectionRole.FrontMatter, suggestion.Role);
        Assert.Equal(0.91, suggestion.Confidence);
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsNarrativeAndSpeechEditsTogether()
    {
        var provider = CreateProvider(
            """
            {"characters":[],"dialogue":[],"edits":[{"originalText":"https://example.test","replacementText":"the example website","rationale":"A raw URL is awkward to hear aloud."}]}
            """);

        var result = await provider.AnalyzeAsync(
            new AnalysisRequest("c1", "Visit https://example.test.", [], NarrationMode.MultiVoice),
            CancellationToken.None);

        var edit = Assert.Single(result.SpeechEdits!);
        Assert.Equal("https://example.test", edit.OriginalText);
        Assert.Equal("the example website", edit.ReplacementText);
        Assert.Contains("awkward", edit.Rationale);
        Assert.Equal("Visit https://example.test.", result.Segments.Single().TextFrom("Visit https://example.test."));
    }

    [Fact]
    public async Task AnalyzeAsync_RetriesMalformedCombinedJsonOnce()
    {
        var completion = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = """{"characters":[],"dialogue":[],"edits":[]}""" } } }
        });
        using var client = new HttpClient(new SequenceHandler(
            """{"choices":[{"message":{"content":"not valid json"}}]}""",
            completion));
        var provider = new OpenAiCompatibleLlmProvider(
            client,
            new Uri("http://localhost:11434/v1/chat/completions"),
            "qwen3:8b");

        var result = await provider.AnalyzeAsync(
            new AnalysisRequest("c1", "Source.", [], NarrationMode.MultiVoice),
            CancellationToken.None);

        Assert.Empty(result.SpeechEdits!);
    }

    [Fact]
    public async Task AnalyzeAsync_SurfacesEndpointFailures()
    {
        using var client = new HttpClient(new StubHandler(
            HttpStatusCode.ServiceUnavailable,
            "model unavailable"));
        var provider = new OpenAiCompatibleLlmProvider(
            client,
            new Uri("http://localhost:11434/v1/chat/completions"),
            "qwen3:8b");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.AnalyzeAsync(
            new AnalysisRequest("c1", "Source.", [], NarrationMode.MultiVoice),
            CancellationToken.None));
        Assert.Contains("503", exception.Message);
        Assert.Contains("model unavailable", exception.Message);
    }

    private static OpenAiCompatibleLlmProvider CreateProvider(string content)
    {
        var completion = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content } } }
        });
        return new OpenAiCompatibleLlmProvider(
            new HttpClient(new StubHandler(HttpStatusCode.OK, completion)),
            new Uri("http://localhost:11434/v1/chat/completions"),
            "qwen3:8b");
    }

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = bodies[Math.Min(_index++, bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
