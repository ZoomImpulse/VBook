using VBook.Core.Narrative;
using VBook.Core.Providers;
using VBook.Core.Worker;
using Xunit;

namespace VBook.Core.Tests;

public sealed class WorkerTests
{
    private const string Text = "“Hello,” said Mary. She smiled.";

    [Fact]
    public void Protocol_RoundTripsEveryMessageType()
    {
        var id = Guid.NewGuid();
        var analysis = new ChapterAnalysis(
            "c1",
            TextFingerprint.Compute(Text),
            [new Character("mary", "Mary", ["Mar"])],
            [new NarrativeSegment(0, SegmentKind.Dialogue, "mary", 0, 8)]);

        WorkerRequest[] requests =
        [
            new AnalyzeChapterRequest(id, "c1", Text, analysis.Characters),
            new CancelRequest(id),
        ];
        WorkerEvent[] events =
        [
            new ProgressEvent(id, 0.5, "half"),
            new AnalysisCompletedEvent(id, analysis),
            new FailedEvent(id, "code", "message"),
            new CancelledEvent(id),
        ];

        foreach (var request in requests)
        {
            var clone = WorkerProtocol.DeserializeRequest(WorkerProtocol.Serialize(request));
            Assert.Equal(request.GetType(), clone.GetType());
            Assert.Equal(id, clone.RequestId);
        }

        foreach (var workerEvent in events)
        {
            var json = WorkerProtocol.Serialize(workerEvent);
            var clone = WorkerProtocol.DeserializeEvent(json);
            Assert.Equal(workerEvent.GetType(), clone.GetType());
        }

        var completed = Assert.IsType<AnalysisCompletedEvent>(
            WorkerProtocol.DeserializeEvent(WorkerProtocol.Serialize(events[1])));
        Assert.Equal("Dialogue", completed.Analysis.Segments[0].Kind.ToString());
        Assert.Equal(["Mar"], completed.Analysis.Characters[0].Aliases);
        Assert.Contains("\"type\":\"analysisCompleted\"", WorkerProtocol.Serialize(events[1]));
        Assert.Contains("\"kind\":\"dialogue\"", WorkerProtocol.Serialize(events[1]));
    }

    [Fact]
    public async Task Client_ReturnsAnalysisAndReportsProgress_ThroughTheProtocol()
    {
        var worker = CreateWorker(new MockLlmProvider());
        var reports = new List<AnalysisProgress>();

        var analysis = await worker.AnalyzeChapterAsync(
            new AnalysisRequest("c1", Text, []), new SyncProgress(reports.Add), default);

        Assert.Equal("mary", analysis.Segments[0].SpeakerId);
        Assert.NotEmpty(reports);
        Assert.True(worker.Capabilities.IsMock);
    }

    [Fact]
    public async Task Client_MapsProviderFailuresToWorkerException()
    {
        var worker = CreateWorker(new ThrowingProvider());

        var exception = await Assert.ThrowsAsync<WorkerException>(
            () => worker.AnalyzeChapterAsync(new AnalysisRequest("c1", Text, []), null, default));

        Assert.Equal("internal_error", exception.Code);
    }

    [Fact]
    public async Task Client_ReportsInvalidProviderOutputWithDedicatedCode()
    {
        var worker = CreateWorker(new BadCoverageProvider());

        var exception = await Assert.ThrowsAsync<WorkerException>(
            () => worker.AnalyzeChapterAsync(new AnalysisRequest("c1", Text, []), null, default));

        Assert.Equal("invalid_output", exception.Code);
    }

    [Fact]
    public async Task Client_PropagatesCancellation()
    {
        var worker = CreateWorker(new MockLlmProvider());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.AnalyzeChapterAsync(new AnalysisRequest("c1", Text, []), null, cancellation.Token));
    }

    [Fact]
    public async Task MockTts_ProducesValidWavWhoseLengthScalesWithText()
    {
        var tts = new MockTtsProvider();

        var short1 = await tts.SynthesizeAsync(new SpeechRequest("Hello there.", "mary"), default);
        var long1 = await tts.SynthesizeAsync(new SpeechRequest(new string('a', 600), "mary"), default);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(short1.WavBytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(short1.WavBytes, 8, 4));
        Assert.Equal(44 + (int)(short1.Duration.TotalSeconds * 16_000) * 2, short1.WavBytes.Length);
        Assert.True(long1.Duration > short1.Duration);
        await Assert.ThrowsAsync<ArgumentException>(
            () => tts.SynthesizeAsync(new SpeechRequest(new string('a', 5000), "mary"), default));
    }

    private static ProtocolNarrativeWorker CreateWorker(ILlmProvider provider) =>
        new(new LocalWorkerHost(new ChapterAnalyzer(provider)), provider.Capabilities);

    private sealed class SyncProgress(Action<AnalysisProgress> report) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }

    private sealed class ThrowingProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("throwing", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("model crashed");
    }

    private sealed class BadCoverageProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("bad", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SegmentationResult([], []));
    }
}
