using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VBook.Core.Narrative;
using VBook.Core.Providers;

namespace VBook.Core.Worker;

public interface IWorkerTransport
{
    /// <summary>
    /// Sends a request and streams its events until a terminal event. Cancelling
    /// the token must stop the work (out-of-process transports send a <see cref="CancelRequest"/>).
    /// </summary>
    IAsyncEnumerable<WorkerEvent> SendAsync(WorkerRequest request, CancellationToken cancellationToken);
}

/// <summary>The app-facing contract for chapter analysis, independent of where it runs.</summary>
public interface INarrativeWorker
{
    ProviderCapabilities Capabilities { get; }

    Task<ChapterAnalysis> AnalyzeChapterAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class WorkerException : Exception
{
    public WorkerException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>Runs requests in-process through the same event stream a remote worker would produce.</summary>
public sealed class LocalWorkerHost : IWorkerTransport
{
    private readonly ChapterAnalyzer _analyzer;

    public LocalWorkerHost(ChapterAnalyzer analyzer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public async IAsyncEnumerable<WorkerEvent> SendAsync(
        WorkerRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var channel = Channel.CreateUnbounded<WorkerEvent>();
        var work = Task.Run(() => RunAsync(request, channel.Writer, cancellationToken), CancellationToken.None);

        await foreach (var workerEvent in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return workerEvent;
        }

        await work;
    }

    private async Task RunAsync(WorkerRequest request, ChannelWriter<WorkerEvent> writer, CancellationToken cancellationToken)
    {
        try
        {
            switch (request)
            {
                case AnalyzeChapterRequest analyze:
                    var progress = new InlineProgress(report =>
                        writer.TryWrite(new ProgressEvent(request.RequestId, report.Fraction, report.Message)));
                    var analysis = await _analyzer.AnalyzeAsync(
                        new AnalysisRequest(analyze.ChapterId, analyze.Text, analyze.KnownCharacters),
                        progress,
                        cancellationToken);
                    writer.TryWrite(new AnalysisCompletedEvent(request.RequestId, analysis));
                    break;
                case CancelRequest:
                    // In-process cancellation is driven by the token passed to SendAsync.
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            writer.TryWrite(new CancelledEvent(request.RequestId));
        }
        catch (InvalidDataException exception)
        {
            writer.TryWrite(new FailedEvent(request.RequestId, "invalid_output", exception.Message));
        }
        catch (Exception exception)
        {
            writer.TryWrite(new FailedEvent(request.RequestId, "internal_error", exception.Message));
        }
        finally
        {
            writer.Complete();
        }
    }

    private sealed class InlineProgress(Action<AnalysisProgress> report) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }
}

public sealed class ProtocolNarrativeWorker : INarrativeWorker
{
    private readonly IWorkerTransport _transport;

    public ProtocolNarrativeWorker(IWorkerTransport transport, ProviderCapabilities capabilities)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public ProviderCapabilities Capabilities { get; }

    public async Task<ChapterAnalysis> AnalyzeChapterAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = Guid.NewGuid();
        var message = new AnalyzeChapterRequest(requestId, request.ChapterId, request.Text, request.KnownCharacters);
        await foreach (var workerEvent in _transport.SendAsync(message, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (workerEvent.RequestId != requestId)
            {
                continue;
            }

            switch (workerEvent)
            {
                case ProgressEvent update:
                    progress?.Report(new AnalysisProgress(update.Fraction, update.Message));
                    break;
                case AnalysisCompletedEvent completed:
                    return completed.Analysis;
                case FailedEvent failed:
                    throw new WorkerException(failed.Code, failed.Message);
                case CancelledEvent:
                    throw new OperationCanceledException(cancellationToken);
            }
        }

        throw new WorkerException("stream_ended", "The worker ended without returning a result.");
    }
}
