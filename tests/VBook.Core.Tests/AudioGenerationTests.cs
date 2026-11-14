using System.Text;
using System.Diagnostics;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;
using Xunit;

namespace VBook.Core.Tests;

public sealed class AudioGenerationTests
{
    [Fact]
    public void ResolveEffectiveVoiceProfiles_RequiresBookDefaultAndValidPerVoiceLanguages()
    {
        var service = new AudioGenerationService(
            new SqliteProjectStore(Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}.sqlite3")),
            new MockTtsProvider());
        var profiles = new Dictionary<string, SpeechVoiceProfile>
        {
            ["narrator"] = new("mock-warm", string.Empty, string.Empty)
        };

        Assert.False(service.TryResolveEffectiveVoiceProfiles(
            profiles,
            string.Empty,
            out _,
            out var missingDefaultError));
        Assert.Contains("book language", missingDefaultError, StringComparison.OrdinalIgnoreCase);

        Assert.True(service.TryResolveEffectiveVoiceProfiles(
            profiles,
            "English",
            out var inheritedProfiles,
            out _));
        Assert.Equal("English", inheritedProfiles["narrator"].Language);

        Assert.False(service.TryResolveEffectiveVoiceProfiles(
            new Dictionary<string, SpeechVoiceProfile>
            {
                ["narrator"] = new("mock-warm", "Spanish", string.Empty)
            },
            "English",
            out _,
            out var unsupportedOverrideError));
        Assert.Contains("does not support", unsupportedOverrideError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SegmentGeneration_ReusesOtherSegmentsAndRebuildsCompletedChapter()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            const string text = "One. Two.";
            await store.SaveAsync(new AudiobookProject(projectId, "Book", "book.epub",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new ImportedChapter("c1", "Chapter", text)], NarrationMode.SingleNarrator));
            var analysis = new ChapterAnalysis("c1", TextFingerprint.Compute(text), [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, 5),
                 new NarrativeSegment(1, SegmentKind.Narration, null, 5, 4)], NarrationMode.SingleNarrator);
            var provider = new RecordingTtsProvider();
            var service = new AudioGenerationService(store, provider);
            var assignments = new Dictionary<string, string> { ["narrator"] = "mock-warm" };
            var reported = new List<GeneratedAudioSegment>();
            var progress = new SegmentProgress(reported);

            var partial = await service.GenerateSegmentAsync(projectId, "c1", text, analysis,
                NarrationMode.SingleNarrator, assignments, 1, CancellationToken.None,
                defaultLanguage: "English", segmentProgress: progress);
            Assert.Null(partial);
            Assert.Equal(["Two."], provider.Texts);
            Assert.Equal(1, Assert.Single(reported).Index);

            reported.Clear();
            var chapter = await service.GenerateChapterAsync(projectId, "c1", text, analysis,
                NarrationMode.SingleNarrator, assignments, null, CancellationToken.None,
                defaultLanguage: "English", segmentProgress: progress);
            Assert.Equal(["Two.", "One."], provider.Texts);
            Assert.Equal([0, 1], reported.Select(item => item.Index));

            reported.Clear();
            var regenerated = await service.GenerateSegmentAsync(projectId, "c1", text, analysis,
                NarrationMode.SingleNarrator, assignments, 1, CancellationToken.None,
                defaultLanguage: "English", segmentProgress: progress);
            Assert.NotNull(regenerated);
            Assert.Equal(chapter.Job.Id, regenerated.Job.Id);
            Assert.Equal(["Two.", "One.", "Two."], provider.Texts);
            Assert.Equal([0, 1], reported.Select(item => item.Index));
            Assert.Equal(2, (await store.LoadGeneratedSegmentsAsync(chapter.Job.Id)).Count);
            Assert.Equal(GenerationJobStatus.Completed, regenerated.Job.Status);
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    private sealed class SegmentProgress(List<GeneratedAudioSegment> segments) : IProgress<GeneratedAudioSegment>
    {
        public void Report(GeneratedAudioSegment value) => segments.Add(value);
    }

    [Fact]
    public async Task Generation_PersistsCompletedSegmentsAndResumesAfterCancellation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Book",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new VBook.Core.Books.ImportedChapter("c1", "Chapter", "One. Two.")],
                NarrationMode.SingleNarrator));
            var text = "One. Two.";
            var analysis = new ChapterAnalysis(
                "c1",
                TextFingerprint.Compute(text),
                [],
                [
                    new NarrativeSegment(0, SegmentKind.Narration, null, 0, 5),
                    new NarrativeSegment(1, SegmentKind.Narration, null, 5, 4)
                ],
                NarrationMode.SingleNarrator);
            var firstProvider = new CancellingTtsProvider();
            var firstService = new AudioGenerationService(store, firstProvider);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstService.GenerateChapterAsync(
                projectId,
                "c1",
                text,
                analysis,
                NarrationMode.SingleNarrator,
                new Dictionary<string, string> { ["narrator"] = "mock-warm" },
                new CancellingProgress(cancellation),
                cancellation.Token,
                defaultLanguage: "English"));

            string pausedJobId;
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM GenerationJobs WHERE ProjectId = $projectId AND ChapterId = $chapterId AND Status = 'Paused';";
                command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
                command.Parameters.AddWithValue("$chapterId", "c1");
                pausedJobId = (string?)await command.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("The cancelled job was not persisted as paused.");
            }
            Assert.Single(await store.LoadGeneratedSegmentsAsync(Guid.Parse(pausedJobId)));

            var resumed = await new AudioGenerationService(store, new MockTtsProvider()).GenerateChapterAsync(
                projectId,
                "c1",
                text,
                analysis,
                NarrationMode.SingleNarrator,
                new Dictionary<string, string> { ["narrator"] = "mock-warm" },
                null,
                CancellationToken.None,
                defaultLanguage: "English");

            Assert.Equal(GenerationJobStatus.Completed, resumed.Job.Status);
            Assert.Equal(Guid.Parse(pausedJobId), resumed.Job.Id);
            Assert.Equal(2, (await store.LoadGeneratedSegmentsAsync(resumed.Job.Id)).Count);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(resumed.WavBytes, 0, 4));
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Generation_PrefersSentenceBoundariesWhenSplittingLongText()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            var text = "First sentence ends. Second sentence is longer than the limit.";
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Book",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ImportedChapter("c1", "Chapter", text)],
                NarrationMode.SingleNarrator));
            var analysis = new ChapterAnalysis(
                "c1",
                TextFingerprint.Compute(text),
                [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, text.Length)],
                NarrationMode.SingleNarrator);
            var provider = new ShortContextRecordingTtsProvider(28);

            await new AudioGenerationService(store, provider).GenerateChapterAsync(
                projectId,
                "c1",
                text,
                analysis,
                NarrationMode.SingleNarrator,
                new Dictionary<string, string> { ["narrator"] = "mock-warm" },
                progress: null,
                CancellationToken.None,
                defaultLanguage: "English");

            Assert.Equal(text, string.Concat(provider.Texts));
            Assert.All(provider.Texts, part => Assert.True(part.Length <= 28));
            Assert.StartsWith("First sentence ends. ", provider.Texts[0]);
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Generation_UsesAcceptedSpeechEditWithoutChangingSource()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            const string source = "Visit https://example.test.";
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Book",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new VBook.Core.Books.ImportedChapter("c1", "Chapter", source)],
                NarrationMode.SingleNarrator));
            var analysis = new ChapterAnalysis(
                "c1",
                TextFingerprint.Compute(source),
                [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, source.Length)],
                NarrationMode.SingleNarrator);
            var start = source.IndexOf("https://example.test", StringComparison.Ordinal);
            var review = new SpeechEditReview(
                "c1",
                TextFingerprint.Compute(source),
                [
                    new SpeechEditSuggestion(
                        Guid.NewGuid(),
                        start,
                        "https://example.test".Length,
                        "https://example.test",
                        "the example website",
                        "A URL is awkward to hear.",
                        SpeechEditStatus.Accepted)
                ]);
            var tts = new RecordingTtsProvider();

            await new AudioGenerationService(store, tts).GenerateChapterAsync(
                projectId,
                "c1",
                source,
                analysis,
                NarrationMode.SingleNarrator,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None,
                speechReview: review,
                defaultLanguage: "English");

            Assert.Equal("Visit the example website.", Assert.Single(tts.Texts));
            Assert.Equal("Visit https://example.test.", source);
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task WholeBookGeneration_AnalyzesAndGeneratesOnlyNarratableChapters()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            var chapters = new[]
            {
                new ImportedChapter("c1", "Chapter One", "A short chapter."),
                new ImportedChapter("c2", "Contents", "Navigation.", SectionRole.Skip),
                new ImportedChapter("c3", "Chapter Two", "Another short chapter.")
            };
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Book",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                chapters,
                NarrationMode.SingleNarrator));

            var llm = new MockLlmProvider();
            var worker = new ProtocolNarrativeWorker(new LocalWorkerHost(new ChapterAnalyzer(llm)), llm.Capabilities);
            var tts = new MockTtsProvider();
            var service = new BookAudioGenerationService(
                worker,
                store,
                new AudioGenerationService(store, tts));

            using var book = await service.GenerateAsync(
                projectId,
                "Book",
                chapters,
                NarrationMode.SingleNarrator,
                new Dictionary<string, string>(),
                null,
                CancellationToken.None,
                defaultLanguage: "English");

            Assert.Equal(["c1", "c3"], book.Chapters.Select(chapter => chapter.Id));
            Assert.All(book.Chapters, chapter =>
                Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(chapter.WavPath), 0, 4)));
            Assert.NotNull(await store.LoadAnalysisAsync(projectId, "c1"));
            Assert.NotNull(await store.LoadSpeechReviewAsync(projectId, "c3"));
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM GenerationJobs WHERE ProjectId = $project;";
            command.Parameters.AddWithValue("$project", projectId.ToString("D"));
            Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
            var wavPaths = book.Chapters.Select(chapter => chapter.WavPath).ToArray();
            book.Dispose();
            Assert.All(wavPaths, wavPath => Assert.False(File.Exists(wavPath)));
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public async Task M4bExporter_WritesEscapedChapterMetadataAndInvokesFfmpeg()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vbook-m4b-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var runner = new RecordingFfmpegRunner();
            var exporter = new FfmpegM4bExporter("ffmpeg", runner);
            var firstWavPath = Path.Combine(directory, "first.wav");
            var secondWavPath = Path.Combine(directory, "second.wav");
            await File.WriteAllBytesAsync(firstWavPath, WavPcm.CreateSilence(TimeSpan.FromMilliseconds(20)));
            await File.WriteAllBytesAsync(secondWavPath, WavPcm.CreateSilence(TimeSpan.FromMilliseconds(40)));
            var book = new GeneratedBookAudio(
                "A; #= Book",
                [
                    new GeneratedBookChapter("c1", "First\nChapter", firstWavPath, TimeSpan.Zero),
                    new GeneratedBookChapter("c2", "Second", secondWavPath, TimeSpan.Zero)
                ]);
            var outputPath = Path.Combine(directory, "book.m4b");

            await exporter.ExportAsync(book, outputPath, CancellationToken.None);

            Assert.True(File.Exists(outputPath));
            Assert.Equal([0x4D, 0x34, 0x42], await File.ReadAllBytesAsync(outputPath));
            Assert.Equal("ffmpeg", runner.ExecutablePath);
            Assert.Contains("-map_chapters", runner.Arguments);
            Assert.EndsWith(".m4b", runner.Arguments[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("title=A\\; \\#\\= Book", runner.Metadata);
            Assert.Contains("title=First\\nChapter", runner.Metadata);
            Assert.Contains("START=20", runner.Metadata);
            Assert.Contains("aresample=44100", runner.FilterGraph);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task M4bExporter_EncodesChapterMarkersForDifferentSampleRatesWhenFfmpegIsAvailable()
    {
        if (!CanStartExecutable("ffmpeg") || !CanStartExecutable("ffprobe"))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"vbook-m4b-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var outputPath = Path.Combine(directory, "book.m4b");
            var firstWavPath = Path.Combine(directory, "first.wav");
            var secondWavPath = Path.Combine(directory, "second.wav");
            await File.WriteAllBytesAsync(firstWavPath, WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250), 8_000));
            await File.WriteAllBytesAsync(secondWavPath, WavPcm.CreateSilence(TimeSpan.FromMilliseconds(300), 22_050));
            var book = new GeneratedBookAudio(
                "Integration book",
                [
                    new GeneratedBookChapter("c1", "First chapter", firstWavPath, TimeSpan.Zero),
                    new GeneratedBookChapter("c2", "Second chapter", secondWavPath, TimeSpan.Zero)
                ]);
            await new FfmpegM4bExporter("ffmpeg").ExportAsync(book, outputPath, CancellationToken.None);

            var startInfo = new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "-v", "error", "-show_entries", "chapter_tags=title", "-of", "csv=p=0", outputPath
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start ffprobe.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            var chapters = await output;
            Assert.Contains("First chapter", chapters, StringComparison.Ordinal);
            Assert.Contains("Second chapter", chapters, StringComparison.Ordinal);
            Assert.Empty(await error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WavPcm_RejectsInvalidWaveData()
    {
        Assert.Throws<InvalidDataException>(() => WavPcm.Concatenate([new byte[44]], TimeSpan.Zero));
    }

    [Fact]
    public async Task Generation_UsesLanguageAndInstructionAndInvalidatesAudioWhenProfileChanges()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"vbook-test-{Guid.NewGuid():N}", "projects.sqlite3");
        try
        {
            var store = new SqliteProjectStore(databasePath);
            await store.InitializeAsync();
            var projectId = Guid.NewGuid();
            const string text = "Hello there.";
            await store.SaveAsync(new AudiobookProject(
                projectId,
                "Book",
                "book.epub",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ImportedChapter("c1", "Chapter", text)],
                NarrationMode.SingleNarrator));
            var analysis = new ChapterAnalysis(
                "c1",
                TextFingerprint.Compute(text),
                [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, text.Length)],
                NarrationMode.SingleNarrator);
            var provider = new RecordingTtsProvider();
            var service = new AudioGenerationService(store, provider);
            var assignments = new Dictionary<string, string> { ["narrator"] = "mock-warm" };
            var referenceAudio = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250));
            const string referenceText = "This is the original voice sample.";
            var firstProfile = new Dictionary<string, SpeechVoiceProfile>
            {
                ["narrator"] = new("mock-warm", "English", "Speak warmly.", referenceAudio, referenceText)
            };

            var first = await service.GenerateChapterAsync(
                projectId, "c1", text, analysis, NarrationMode.SingleNarrator, assignments,
                null, CancellationToken.None, voiceProfiles: firstProfile, defaultLanguage: "English");
            await service.GenerateChapterAsync(
                projectId, "c1", text, analysis, NarrationMode.SingleNarrator, assignments,
                null, CancellationToken.None, voiceProfiles: firstProfile, defaultLanguage: "English");
            var changedProfile = new Dictionary<string, SpeechVoiceProfile>
            {
                ["narrator"] = new("mock-warm", "German", "Speak softly.")
            };
            var changed = await service.GenerateChapterAsync(
                projectId, "c1", text, analysis, NarrationMode.SingleNarrator, assignments,
                null, CancellationToken.None, voiceProfiles: changedProfile, defaultLanguage: "English");
            var changedReferenceAudio = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(300));
            var changedReferenceProfile = new Dictionary<string, SpeechVoiceProfile>
            {
                ["narrator"] = new(
                    "mock-warm",
                    "German",
                    "Speak softly.",
                    changedReferenceAudio,
                    "This is the updated voice sample.")
            };
            var changedReference = await service.GenerateChapterAsync(
                projectId, "c1", text, analysis, NarrationMode.SingleNarrator, assignments,
                null, CancellationToken.None, voiceProfiles: changedReferenceProfile, defaultLanguage: "English");

            Assert.NotEqual(first.Job.Id, changed.Job.Id);
            Assert.NotEqual(changed.Job.Id, changedReference.Job.Id);
            Assert.Equal(3, provider.Requests.Count);
            Assert.Equal(("English", "Speak warmly."), (provider.Requests[0].Language, provider.Requests[0].Instruction));
            Assert.Equal(("German", "Speak softly."), (provider.Requests[1].Language, provider.Requests[1].Instruction));
            Assert.Equal(referenceText, provider.Requests[0].VoiceDesignReferenceText);
            Assert.Equal(
                Convert.ToBase64String(referenceAudio),
                Convert.ToBase64String(provider.Requests[0].VoiceDesignReferenceAudio!));
            Assert.Equal(
                Convert.ToBase64String(changedReferenceAudio),
                Convert.ToBase64String(provider.Requests[2].VoiceDesignReferenceAudio!));
        }
        finally
        {
            DeleteDirectory(databasePath);
        }
    }

    [Fact]
    public void WavPcm_ReadPcmAudioReturnsTheFormatAndSampleData()
    {
        var wav = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(20), 16_000);

        var audio = WavPcm.ReadPcmAudio(wav);

        Assert.Equal((short)1, audio.Channels);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal((short)2, audio.BlockAlign);
        Assert.Equal((short)16, audio.BitsPerSample);
        Assert.Equal(640, audio.Data.Length);
    }

    private static void DeleteDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool CanStartExecutable(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            standardOutput.GetAwaiter().GetResult();
            standardError.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class CancellingTtsProvider : ITtsProvider
    {
        private readonly MockTtsProvider _inner = new();

        public ProviderCapabilities Capabilities => _inner.Capabilities;

        public IReadOnlyList<string> VoiceIds => _inner.VoiceIds;

        public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken) =>
            _inner.SynthesizeAsync(request, cancellationToken);
    }

    private sealed class RecordingTtsProvider : ITtsProvider
    {
        private readonly MockTtsProvider _inner = new();

        public List<string> Texts { get; } = [];

        public List<SpeechRequest> Requests { get; } = [];

        public ProviderCapabilities Capabilities => _inner.Capabilities;

        public IReadOnlyList<string> VoiceIds => _inner.VoiceIds;

        public IReadOnlyList<string> Languages { get; } = ["English", "German"];

        public bool SupportsVoiceInstructions => true;

        public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            Texts.Add(request.Text);
            Requests.Add(request);
            return _inner.SynthesizeAsync(request with { Language = "English" }, cancellationToken);
        }

    }

    private sealed class ShortContextRecordingTtsProvider(int maxContextCharacters) : ITtsProvider
    {
        private readonly MockTtsProvider _inner = new();

        public List<string> Texts { get; } = [];

        public ProviderCapabilities Capabilities =>
            new("short context recorder", IsMock: true, maxContextCharacters);

        public IReadOnlyList<string> VoiceIds => _inner.VoiceIds;

        public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            Texts.Add(request.Text);
            return _inner.SynthesizeAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingFfmpegRunner : IFfmpegProcessRunner
    {
        public string ExecutablePath { get; private set; } = string.Empty;

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string Metadata { get; private set; } = string.Empty;

        public string FilterGraph { get; private set; } = string.Empty;

        public async Task RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            ExecutablePath = executablePath;
            Arguments = arguments;
            var metadataIndex = Array.FindLastIndex(arguments.ToArray(), argument => argument == "-i") + 1;
            Metadata = await File.ReadAllTextAsync(arguments[metadataIndex], cancellationToken);
            var filterIndex = Array.IndexOf(arguments.ToArray(), "-filter_complex_script") + 1;
            FilterGraph = await File.ReadAllTextAsync(arguments[filterIndex], cancellationToken);
            await File.WriteAllBytesAsync(arguments[^1], [0x4D, 0x34, 0x42], cancellationToken);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value)
        {
            if (value.CompletedSegments == 1)
            {
                cancellation.Cancel();
            }
        }
    }
}
