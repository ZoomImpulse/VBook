using VBook.App.Services;
using VBook.App.ViewModels;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;
using Xunit;

namespace VBook.App.Tests;

public sealed class NarrativeViewModelTests : IAsyncLifetime
{
    private const string Chapter1 = "“Hello,” said Mary.\n\nTom nodded. “Hi,” said Tom.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vbook-app-test-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void VoiceAssignmentItem_ShowsNativeLanguageWithoutChangingVoiceIds()
    {
        var assignment = new VoiceAssignmentItem(
            "narrator",
            "Narrator",
            ["Aiden", "Vivian"],
            voiceId => voiceId == "Aiden" ? "English" : "Chinese",
            ["English", "Chinese"],
            supportsInstructions: true,
            profile: new SpeechVoiceProfile("Aiden", string.Empty, string.Empty));

        Assert.Equal(["Aiden (native: English)", "Vivian (native: Chinese)"], assignment.VoiceOptions);
        Assert.Equal("Aiden (native: English)", assignment.VoiceSelection);

        assignment.VoiceSelection = "Vivian (native: Chinese)";

        Assert.Equal("Vivian", assignment.VoiceId);
        Assert.Equal("Vivian (native: Chinese)", assignment.VoiceSelection);
    }

    [Fact]
    public async Task Analyze_ShowsSegmentsAndCharacters_AndSurvivesReopen()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasAnalysis);
        Assert.Equal(
            ["Mary", "Narrator", "Tom"],
            viewModel.Segments.Select(segment => segment.Speaker).Distinct().Order());
        Assert.Equal(["Mary", "Tom"], viewModel.Characters.Select(c => c.Name).Order());
        Assert.Contains("segments", viewModel.AnalysisStatus);

        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.HasAnalysis);
        Assert.Equal(viewModel.Segments.Count, reopened.Segments.Count);
    }

    [Fact]
    public async Task WorkflowGuide_AdvancesThroughFourBookPhasesAndAllowsDirectNavigation()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());

        Assert.Equal(1, viewModel.WorkflowStepNumber);
        Assert.Equal("Prepare", viewModel.WorkflowStepTitle);
        Assert.Equal(0.25, viewModel.WorkflowProgress);
        Assert.True(viewModel.ContinueWorkflowCommand.CanExecute(null));

        for (var step = 1; step < viewModel.WorkflowStepCount; step++)
        {
            viewModel.ContinueWorkflowCommand.Execute(null);
            Assert.Equal(step, viewModel.SelectedWorkflowStepIndex);
        }

        Assert.Equal("Publish", viewModel.WorkflowStepTitle);
        Assert.Equal(1, viewModel.WorkflowProgress);
        Assert.False(viewModel.ContinueWorkflowCommand.CanExecute(null));

        viewModel.SelectedWorkflowStepIndex = 2;

        Assert.Equal("Produce", viewModel.WorkflowStepTitle);
        Assert.Equal("Continue to Publish", viewModel.ContinueWorkflowLabel);
        Assert.True(viewModel.ContinueWorkflowCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProductionQueue_ReflectsAnalysisAndCompletedAudioReadiness()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());
        Assert.Equal("Needs analysis", Assert.Single(viewModel.ChapterQueue).Status);
        viewModel.QueueSearchText = "not a chapter";
        Assert.Empty(viewModel.VisibleChapterQueue);
        Assert.True(viewModel.IsQueueSearchEmpty);
        viewModel.QueueSearchText = string.Empty;
        Assert.Single(viewModel.VisibleChapterQueue);
        Assert.False(viewModel.IsQueueSearchEmpty);
        viewModel.BookLanguage = "English";

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.Equal("Ready to generate", Assert.Single(viewModel.ChapterQueue).Status);
        Assert.True(viewModel.GenerateChapterCommand.CanExecute(null));
        await viewModel.GenerateChapterCommand.ExecuteAsync(null);

        Assert.True(
            Assert.Single(viewModel.ChapterQueue).Status == "Audio ready",
            $"Queue status: {Assert.Single(viewModel.ChapterQueue).Status}; generation: {viewModel.GenerationStatus}");
        Assert.Equal("1 of 1 narratable chapters have audio.", viewModel.BookReadinessSummary);

        viewModel.ChapterText += " Edited after generation.";
        await viewModel.FlushAsync();
        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        Assert.Equal("Needs analysis", Assert.Single(reopened.ChapterQueue).Status);
    }

    [Fact]
    public async Task ProduceActions_KeepAnalysisAndEngineSetupAvailableBeforeAudioIsReady()
    {
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        using var ttsProvider = new LocalTtsProviderService(
            new KokoroModelAssetManager(client, Path.Combine(_directory, "models")),
            new FakeKokoroSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            Path.Combine(_directory, "tts-settings.json"));
        var store = new SqliteProjectStore(Path.Combine(_directory, "produce-setup.sqlite3"));
        using var viewModel = CreateViewModel(store, new MockLlmProvider(), ttsProvider: ttsProvider);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);

        Assert.True(viewModel.AnalyzeChapterCommand.CanExecute(null));
        Assert.True(viewModel.DownloadKokoroCommand.CanExecute(null));
        Assert.False(viewModel.GenerateChapterCommand.CanExecute(null));
        Assert.False(viewModel.GenerateBookCommand.CanExecute(null));
    }

    [Fact]
    public void SetupQwenCommand_UpdatesEnabledStateWhenGenerationStartsAndStops()
    {
        using var client = new HttpClient(new EmptyHttpMessageHandler());
        using var ttsProvider = new LocalTtsProviderService(
            new KokoroModelAssetManager(client, Path.Combine(_directory, "models")),
            new FakeKokoroSpeechEngineFactory(),
            piperExecutable: null,
            piperVoicesDirectory: null,
            Path.Combine(_directory, "tts-settings.json"));
        ttsProvider.SelectProvider(LocalTtsProviderService.QwenProviderId);
        var store = new SqliteProjectStore(Path.Combine(_directory, "qwen-setup-command.sqlite3"));
        using var viewModel = CreateViewModel(store, new MockLlmProvider(), ttsProvider: ttsProvider);
        var canExecuteChangedCount = 0;
        viewModel.SetupQwenCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        Assert.True(viewModel.IsQwenSetupRequired);
        Assert.True(viewModel.SetupQwenCommand.CanExecute(null));

        viewModel.IsGenerating = true;

        Assert.False(viewModel.SetupQwenCommand.CanExecute(null));
        Assert.Equal(1, canExecuteChangedCount);

        viewModel.IsGenerating = false;

        Assert.True(viewModel.SetupQwenCommand.CanExecute(null));
        Assert.Equal(2, canExecuteChangedCount);
    }

    [Fact]
    public async Task ChapterAnalysisGuidance_ExplainsSelectionSkippedSectionsAndBusyState()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        var chapter = Assert.IsType<ImportedChapter>(viewModel.SelectedChapter);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Assert.Contains(chapter.Title, viewModel.ChapterAnalysisGuidance);
        Assert.True(viewModel.AnalyzeChapterCommand.CanExecute(null));

        viewModel.SelectedChapter = null;
        Assert.True(viewModel.HasBookWithoutSelectedChapter);
        Assert.False(viewModel.HasNoBook);
        Assert.Contains("Select a chapter", viewModel.ChapterAnalysisGuidance);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));
        Assert.Contains(nameof(viewModel.ChapterAnalysisGuidance), changes);
        Assert.Contains(nameof(viewModel.HasBookWithoutSelectedChapter), changes);

        viewModel.SelectedChapter = chapter with { Role = SectionRole.Skip };
        Assert.Contains("marked Skip", viewModel.ChapterAnalysisGuidance);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));

        viewModel.SelectedChapter = chapter;
        changes.Clear();
        viewModel.IsBusy = true;
        Assert.Contains("Wait", viewModel.ChapterAnalysisGuidance);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));
        Assert.Contains(nameof(viewModel.ChapterAnalysisGuidance), changes);
        viewModel.IsBusy = false;
        Assert.True(viewModel.AnalyzeChapterCommand.CanExecute(null));
        Assert.Contains(chapter.Title, viewModel.ChapterAnalysisGuidance);
    }

    [Fact]
    public async Task NarrationModeAndVoiceProfiles_PersistAcrossReopen()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());
        viewModel.UseMultipleVoices = false;
        await viewModel.FlushAsync();

        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.SelectedChapter is not null);
        Assert.False(reopened.UseMultipleVoices);

        await reopened.AnalyzeChapterCommand.ExecuteAsync(null);
        var narrator = Assert.Single(reopened.VoiceAssignments);
        narrator.VoiceId = "mock-deep";
        await WaitUntilAsync(async () =>
            (await store.LoadVoiceAssignmentsAsync(viewModel.RecentProjects.Single().Id))
            .GetValueOrDefault("narrator") == "mock-deep");
        narrator.Instruction = "Speak with a calm, measured pace.";
        await WaitUntilAsync(async () =>
            (await store.LoadVoiceProfilesAsync(
                viewModel.RecentProjects.Single().Id,
                "Mock tone synthesizer (no model)"))
            .GetValueOrDefault("narrator")?.Instruction == "Speak with a calm, measured pace.");

        var reopenedAgain = CreateViewModel(store, new MockLlmProvider());
        await reopenedAgain.InitializeAsync();
        await WaitUntilAsync(() => reopenedAgain.VoiceAssignments.Count == 1);
        Assert.Equal("mock-deep", Assert.Single(reopenedAgain.VoiceAssignments).VoiceId);
        Assert.Equal("Speak with a calm, measured pace.", reopenedAgain.VoiceAssignments.Single().Instruction);
    }

    [Fact]
    public async Task PreviewVoice_SynthesizesSelectedProfileAndStartsPlayback()
    {
        var provider = new RecordingTtsProvider();
        var audioPlayback = new FakeAudioPlaybackService();
        var store = new SqliteProjectStore(Path.Combine(_directory, "voice-preview.sqlite3"));
        using var viewModel = CreateViewModel(
            store,
            new MockLlmProvider(),
            audioPlayback: audioPlayback,
            ttsProvider: provider);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        viewModel.BookLanguage = "English";

        var assignment = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerId == "narrator");
        assignment.VoiceId = "mock-deep";
        assignment.Language = "Spanish";
        assignment.Instruction = "Speak warmly.";

        Assert.True(viewModel.PreviewVoiceCommand.CanExecute(assignment));
        await viewModel.PreviewVoiceCommand.ExecuteAsync(assignment);

        Assert.Equal(
            new SpeechRequest("Hola, esta es una breve muestra de voz.", "mock-deep", "Spanish", "Speak warmly."),
            provider.LastRequest);
        Assert.NotNull(audioPlayback.LastPlayedAudio);
        Assert.True(audioPlayback.IsPlaying);
        Assert.Contains("Playing the Spanish voice preview", viewModel.GenerationStatus);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task VoiceLanguages_RequireBookDefaultAndPersistPerVoiceOverrides()
    {
        var provider = new RecordingTtsProvider();
        var store = new SqliteProjectStore(Path.Combine(_directory, "voice-languages.sqlite3"));
        using var viewModel = CreateViewModel(
            store,
            new MockLlmProvider(),
            ttsProvider: provider);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        viewModel.UseMultipleVoices = true;
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.False(viewModel.GenerateChapterCommand.CanExecute(null));
        Assert.False(viewModel.GenerateBookCommand.CanExecute(null));
        await WaitUntilAsync(() =>
            Assert.Single(viewModel.ChapterQueue).Status == "Select voice language");

        viewModel.BookLanguage = "English";
        Assert.True(viewModel.GenerateChapterCommand.CanExecute(null));
        await WaitUntilAsync(() =>
            Assert.Single(viewModel.ChapterQueue).Status == "Ready to generate");
        var narrator = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerId == "narrator");
        var character = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerName == "Mary");
        Assert.Equal(VoiceAssignmentItem.BookDefaultLanguageOption, narrator.LanguageSelection);
        character.LanguageSelection = "Spanish";
        await WaitUntilAsync(async () =>
            (await store.LoadVoiceProfilesAsync(
                viewModel.RecentProjects.Single().Id,
                provider.Capabilities.Name))
            .GetValueOrDefault(character.SpeakerId)?.Language == "Spanish");
        await viewModel.FlushAsync();

        using var reopened = CreateViewModel(store, new MockLlmProvider(), ttsProvider: provider);
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.VoiceAssignments.Count == 3);

        Assert.Equal("English", reopened.BookLanguage);
        Assert.Equal(
            VoiceAssignmentItem.BookDefaultLanguageOption,
            Assert.Single(reopened.VoiceAssignments, item => item.SpeakerId == "narrator").LanguageSelection);
        Assert.Equal(
            "Spanish",
            Assert.Single(reopened.VoiceAssignments, item => item.SpeakerName == "Mary").LanguageSelection);
    }

    [Fact]
    public async Task SuggestVoiceInstruction_UsesBookTypeAndVoiceRole()
    {
        var store = new SqliteProjectStore(Path.Combine(_directory, "voice-direction.sqlite3"));
        using var viewModel = CreateViewModel(
            store,
            new MockLlmProvider(),
            ttsProvider: new RecordingTtsProvider());
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        viewModel.UseMultipleVoices = true;
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        var narrator = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerId == "narrator");
        var character = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerName == "Mary");
        Assert.False(viewModel.SuggestVoiceInstructionCommand.CanExecute(narrator));

        viewModel.DetectedGenre = "fantasy";

        Assert.True(viewModel.SuggestVoiceInstructionCommand.CanExecute(narrator));
        viewModel.SuggestVoiceInstructionCommand.Execute(narrator);
        Assert.Equal(
            "Narrate in an immersive and vivid tone. Use measured pacing and suggest subtle wonder. Sound natural, not theatrical.",
            narrator.Instruction);

        viewModel.SuggestVoiceInstructionCommand.Execute(character);
        Assert.Equal(
            "Portray Mary as a distinct, believable character. Use an immersive and vivid tone. Use measured pacing and suggest subtle wonder. Keep the delivery conversational and natural.",
            character.Instruction);

        viewModel.DetectedGenre = "Non-fiction";
        viewModel.SuggestVoiceInstructionCommand.Execute(narrator);
        Assert.Equal(
            "Narrate in a clear, measured, and informative tone. Keep a steady pace, articulate carefully, and use restrained emphasis on key ideas. Sound natural, not theatrical.",
            narrator.Instruction);
    }

    [Fact]
    public async Task VoiceDesigner_StagesLanguageMatchedProfilesAndAppliesPerSpeaker()
    {
        var provider = new RecordingTtsProvider();
        var (viewModel, store) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            ttsProvider: provider);
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        viewModel.BookLanguage = "English";
        viewModel.DetectedGenre = "fantasy";

        var narrator = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerId == "narrator");
        var character = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerName == "Mary");
        character.Language = "Spanish";
        await WaitUntilAsync(async () =>
            (await store.LoadVoiceProfilesAsync(
                viewModel.RecentProjects.Single().Id,
                provider.Capabilities.Name))
            .GetValueOrDefault(character.SpeakerId)?.Language == "Spanish");
        var savedBeforeProposals = character.Profile;

        Assert.True(viewModel.CreateVoiceProposalsCommand.CanExecute(null));
        viewModel.CreateVoiceProposalsCommand.Execute(null);

        Assert.Equal(savedBeforeProposals, character.Profile);
        Assert.Equal(savedBeforeProposals, (await store.LoadVoiceProfilesAsync(
            viewModel.RecentProjects.Single().Id,
            provider.Capabilities.Name))[character.SpeakerId]);
        Assert.Equal("mock-warm", narrator.ProposedVoiceId);
        Assert.Equal("mock-calm", character.ProposedVoiceId);
        Assert.Equal("Spanish", character.ProposedLanguage);
        Assert.Contains("verified native-language metadata", character.RecommendationMessage);
        Assert.StartsWith("Narrate in an immersive and vivid tone.", narrator.ProposedInstruction);
        Assert.StartsWith("Portray Mary as a distinct, believable character.", character.ProposedInstruction);
        Assert.Contains("Use measured pacing", character.ProposedInstruction);
        Assert.True(viewModel.PreviewVoiceProposalCommand.CanExecute(character));
        Assert.False(character.IsProposalApplied);

        await viewModel.PreviewVoiceProposalCommand.ExecuteAsync(character);

        Assert.Equal(
            new SpeechRequest(
                "Hola, esta es una breve muestra de voz.",
                "mock-calm",
                "Spanish",
                character.ProposedInstruction),
            provider.LastRequest);
        Assert.Equal(savedBeforeProposals, character.Profile);

        await viewModel.ApplyVoiceProposalCommand.ExecuteAsync(character);

        Assert.True(character.IsProposalApplied);
        Assert.Equal("mock-calm", character.VoiceId);
        Assert.Equal("Spanish", character.Language);
        Assert.Equal(character.ProposedInstruction, character.Instruction);
        Assert.Equal(character.ProposedProfile, (await store.LoadVoiceProfilesAsync(
            viewModel.RecentProjects.Single().Id,
            provider.Capabilities.Name))[character.SpeakerId]);
        Assert.NotEqual(narrator.ProposedProfile, narrator.Profile);
    }

    [Fact]
    public async Task VoiceDesigner_StagesVoiceDesignThenPersistsBaseCloneReferenceOnApply()
    {
        var provider = new RecordingVoiceDesignProvider();
        var playback = new FakeAudioPlaybackService();
        var (viewModel, store) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            audioPlayback: playback,
            ttsProvider: provider);
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        viewModel.BookLanguage = "English";
        viewModel.DetectedGenre = "fantasy";

        var narrator = Assert.Single(viewModel.VoiceAssignments, item => item.SpeakerId == "narrator");
        viewModel.CreateVoiceProposalsCommand.Execute(null);

        Assert.True(viewModel.SupportsVoiceDesign);
        Assert.Contains("fantasy audiobook", narrator.ProposedInstruction);
        Assert.False(narrator.CanUseProposal);
        Assert.Null(narrator.Profile.VoiceDesignReferenceAudio);
        Assert.True(viewModel.GenerateVoiceDesignCommand.CanExecute(narrator));
        var projectId = viewModel.RecentProjects.Single().Id;
        var initiallySaved = (await store.LoadVoiceProfilesAsync(
            projectId,
            provider.Capabilities.Name))[narrator.SpeakerId];

        await viewModel.GenerateVoiceDesignCommand.ExecuteAsync(narrator);

        Assert.Equal("English", provider.LastDesignRequest!.Language);
        Assert.Equal(narrator.ProposedInstruction, provider.LastDesignRequest.Instruction);
        Assert.Equal(narrator.ProposedSampleText, provider.LastDesignRequest.Text);
        Assert.True(narrator.CanUseProposal);
        Assert.Equal(initiallySaved, (await store.LoadVoiceProfilesAsync(
            projectId,
            provider.Capabilities.Name))[narrator.SpeakerId]);
        Assert.NotNull(playback.LastPlayedAudio);
        Assert.True(viewModel.PreviewVoiceProposalCommand.CanExecute(narrator));

        await viewModel.PreviewVoiceProposalCommand.ExecuteAsync(narrator);

        Assert.Equal(QwenTtsProvider.DesignedVoiceId, provider.LastRequest!.VoiceId);
        Assert.Equal(narrator.ProposedReferenceText, provider.LastRequest.VoiceDesignReferenceText);
        Assert.Equal(
            Convert.ToBase64String(narrator.ProposedReferenceAudio!),
            Convert.ToBase64String(provider.LastRequest.VoiceDesignReferenceAudio!));

        await viewModel.ApplyVoiceProposalCommand.ExecuteAsync(narrator);

        var saved = (await store.LoadVoiceProfilesAsync(
            projectId,
            provider.Capabilities.Name))[narrator.SpeakerId];
        Assert.Equal(narrator.ProposedReferenceText, saved.VoiceDesignReferenceText);
        Assert.Equal(
            Convert.ToBase64String(narrator.ProposedReferenceAudio!),
            Convert.ToBase64String(saved.VoiceDesignReferenceAudio!));
        Assert.True(narrator.IsProposalApplied);
    }

    [Fact]
    public async Task VoiceDesigner_ExplainsMissingNativeMetadataAndKeepsEveryVoiceSelectable()
    {
        var provider = new RecordingTtsProvider(includeNativeLanguageMetadata: false);
        var (viewModel, _) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            ttsProvider: provider);
        viewModel.BookLanguage = "English";
        var narrator = Assert.Single(viewModel.VoiceAssignments);
        var currentVoice = narrator.VoiceId;

        viewModel.CreateVoiceProposalsCommand.Execute(null);

        Assert.Contains("using a general direction", viewModel.VoiceDesignerStatus);
        Assert.Equal(currentVoice, narrator.ProposedVoiceId);
        Assert.Contains("provider supplies no verified native-language metadata", narrator.RecommendationMessage);
        Assert.Equal(provider.VoiceIds, narrator.ProposedVoiceOptions);
        narrator.ProposedVoiceSelection = "mock-bright";
        Assert.Equal("mock-bright", narrator.ProposedVoiceId);
        Assert.False(narrator.IsProposalApplied);
        Assert.Equal(currentVoice, narrator.VoiceId);
    }

    [Fact]
    public async Task VoiceDesigner_ExplainsWhenNoNativeVoiceMatchesTheEffectiveLanguage()
    {
        var provider = new RecordingTtsProvider(includeSpanishNativeMatch: false);
        var (viewModel, _) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            ttsProvider: provider);
        viewModel.BookLanguage = "Spanish";
        viewModel.DetectedGenre = "fantasy";
        var narrator = Assert.Single(viewModel.VoiceAssignments);
        var currentVoice = narrator.VoiceId;

        viewModel.CreateVoiceProposalsCommand.Execute(null);

        Assert.Contains("No voice recommendation is available for Spanish", narrator.RecommendationMessage);
        Assert.Equal(currentVoice, narrator.ProposedVoiceId);
        Assert.Equal(VoiceAssignmentItem.BookDefaultLanguageOption, narrator.ProposedLanguageSelection);
    }

    [Fact]
    public async Task ChangingNarrationMode_MarksAnalysisStaleUntilReanalyzed()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasAnalysis);

        viewModel.UseMultipleVoices = false;

        Assert.True(viewModel.IsAnalysisStale);
        Assert.False(viewModel.HasAnalysis);
        Assert.False(viewModel.GenerateChapterCommand.CanExecute(null));

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsAnalysisStale);
        Assert.True(viewModel.HasAnalysis);
    }

    [Fact]
    public async Task GenerateChapter_CreatesWavThatCanBeExported()
    {
        var exportPath = Path.Combine(_directory, "chapter.wav");
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider(), exportPath);
        viewModel.UseMultipleVoices = false;
        viewModel.BookLanguage = "English";
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        await viewModel.GenerateChapterCommand.ExecuteAsync(null);

        Assert.Contains("Generated", viewModel.GenerationStatus);
        Assert.True(viewModel.PlayGeneratedAudioCommand.CanExecute(null));
        await viewModel.ExportChapterAudioCommand.ExecuteAsync(null);
        Assert.True(File.Exists(exportPath));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(exportPath), 0, 4));
    }

    [Fact]
    public async Task GenerateBook_AnalyzesChaptersAndExportsM4b()
    {
        var exportPath = Path.Combine(_directory, "book.m4b");
        var exporter = new RecordingM4bExporter();
        var store = new SqliteProjectStore(Path.Combine(_directory, "book-export.sqlite3"));
        var viewModel = CreateViewModel(
            store,
            new MockLlmProvider(),
            m4bExportPath: exportPath,
            m4bExporter: exporter);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        viewModel.BookLanguage = "English";

        await viewModel.GenerateBookCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasGeneratedBookAudio);
        await viewModel.ExportBookAudioCommand.ExecuteAsync(null);
        Assert.Equal("Test Book", exporter.LastBook?.Title);
        Assert.Single(exporter.LastBook!.Chapters);
        Assert.True(File.Exists(exportPath));
        Assert.Equal("M4B", System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(exportPath)));
        Assert.Contains("Exported", viewModel.GenerationStatus);
        var temporaryAudioPaths = exporter.LastBook!.Chapters.Select(chapter => chapter.WavPath).ToArray();
        Assert.All(temporaryAudioPaths, wavPath => Assert.True(File.Exists(wavPath)));
        viewModel.Dispose();
        Assert.All(temporaryAudioPaths, wavPath => Assert.False(File.Exists(wavPath)));
    }

    [Fact]
    public async Task ThemePreference_UsesSystemDefaultAndPersistsUserSelection()
    {
        var settingsPath = Path.Combine(_directory, "theme-settings.json");
        var service = new AvaloniaThemePreferenceService(_ => { }, settingsPath);
        var (viewModel, _) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            themePreferences: service);

        Assert.Equal("System", viewModel.ThemeMode);
        Assert.Equal(["System", "Light", "Dark"], viewModel.ThemeModes);

        viewModel.ThemeMode = "Dark";

        Assert.Equal(AppThemePreference.Dark, service.Preference);
        Assert.Equal(AppThemePreference.Dark, new AvaloniaThemePreferenceService(_ => { }, settingsPath).Preference);
    }

    [Fact]
    public async Task EmptyWorkspace_OffersImportInsteadOfBlankChapterEditor()
    {
        var store = new SqliteProjectStore(Path.Combine(_directory, "empty-workspace.sqlite3"));
        var viewModel = CreateViewModel(store, new MockLlmProvider());

        await viewModel.InitializeAsync();

        Assert.Null(viewModel.Book);
        Assert.True(viewModel.HasNoChapter);
        Assert.False(viewModel.HasChapter);
        Assert.Contains("Import", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SegmentAudio_CanBePlayedRegeneratedAndRestored()
    {
        var playback = new FakeAudioPlaybackService();
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider(), audioPlayback: playback);
        viewModel.BookLanguage = "English";
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        var segment = viewModel.Segments[0];

        await viewModel.GenerateSegmentCommand.ExecuteAsync(segment);
        await WaitUntilAsync(() => segment.HasAudio);
        viewModel.PlaySegmentCommand.Execute(segment);
        Assert.True(playback.IsPlaying);
        Assert.True(viewModel.IsAudioPlaying);
        viewModel.PauseGeneratedAudioCommand.Execute(null);
        Assert.True(viewModel.PlayGeneratedAudioCommand.CanExecute(null));
        viewModel.PlayGeneratedAudioCommand.Execute(null);
        Assert.True(playback.IsPlaying);

        await viewModel.GenerateSegmentCommand.ExecuteAsync(segment);
        Assert.True(segment.HasAudio);
        await viewModel.FlushAsync();
        using var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.Segments.Any(item => item.HasAudio));
        Assert.True(reopened.Segments[0].HasAudio);
    }

    [Fact]
    public async Task SegmentAudio_IsPlayableWhileChapterGenerationContinues()
    {
        var provider = new BlockingSecondSegmentTtsProvider();
        var playback = new FakeAudioPlaybackService();
        var (viewModel, _) = await CreateImportedViewModelAsync(
            new MockLlmProvider(), audioPlayback: playback, ttsProvider: provider);
        viewModel.BookLanguage = "English";
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        Assert.True(viewModel.Segments.Count > 1);

        var generation = viewModel.GenerateChapterCommand.ExecuteAsync(null);
        try
        {
            await provider.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => viewModel.Segments[0].HasAudio);
            Assert.True(viewModel.IsGenerating);
            Assert.False(viewModel.GenerateSegmentCommand.CanExecute(viewModel.Segments[0]));
            viewModel.PlaySegmentCommand.Execute(viewModel.Segments[0]);
            Assert.True(playback.IsPlaying);
        }
        finally
        {
            provider.Continue.TrySetResult();
            await generation;
        }
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task AudioPlaybackControls_PlayPauseResumeAndStopGeneratedAudio()
    {
        var playback = new FakeAudioPlaybackService();
        var (viewModel, _) = await CreateImportedViewModelAsync(
            new MockLlmProvider(),
            audioPlayback: playback);
        viewModel.BookLanguage = "English";
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        await viewModel.GenerateChapterCommand.ExecuteAsync(null);

        viewModel.PlayGeneratedAudioCommand.Execute(null);
        Assert.True(playback.IsPlaying);
        Assert.True(viewModel.IsAudioPlaying);

        viewModel.PauseGeneratedAudioCommand.Execute(null);
        Assert.True(playback.IsPaused);
        Assert.True(viewModel.IsAudioPaused);

        viewModel.PlayGeneratedAudioCommand.Execute(null);
        Assert.True(playback.IsPlaying);
        Assert.True(viewModel.IsAudioPlaying);

        viewModel.StopGeneratedAudioCommand.Execute(null);
        Assert.False(playback.IsPlaying);
        Assert.False(viewModel.IsAudioPlaying);
        Assert.True(viewModel.PlayGeneratedAudioCommand.CanExecute(null));
    }

    [Fact]
    public async Task StructureSuggestions_AreReviewedAppliedAndPersisted()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());

        await viewModel.AnalyzeBookStructureCommand.ExecuteAsync(null);

        var suggestion = Assert.Single(viewModel.StructureSuggestions);
        Assert.Equal("Test genre", viewModel.DetectedGenre);
        await viewModel.FlushAsync();

        using var pendingReview = CreateViewModel(store, new MockLlmProvider());
        await pendingReview.InitializeAsync();
        await WaitUntilAsync(() => pendingReview.SelectedChapter is not null);
        Assert.Equal("Test genre", pendingReview.DetectedGenre);
        Assert.Equal(
            viewModel.StructureSuggestions.Select(item => item.SuggestedRole),
            pendingReview.StructureSuggestions.Select(item => item.SuggestedRole));
        Assert.Equal(VBook.Core.Books.SectionRole.Body, pendingReview.SelectedChapter?.Role);

        suggestion.SuggestedRole = VBook.Core.Books.SectionRole.Skip;
        await viewModel.ApplyStructureSuggestionsCommand.ExecuteAsync(null);

        Assert.Equal(VBook.Core.Books.SectionRole.Skip, viewModel.SelectedChapter?.Role);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));

        await viewModel.FlushAsync();
        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.SelectedChapter is not null);

        Assert.Equal("Test genre", reopened.DetectedGenre);
        var restoredSuggestion = Assert.Single(reopened.StructureSuggestions);
        Assert.Equal(VBook.Core.Books.SectionRole.Skip, restoredSuggestion.SuggestedRole);
        Assert.Equal(VBook.Core.Books.SectionRole.Skip, reopened.SelectedChapter?.Role);
        Assert.False(reopened.AnalyzeChapterCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditingText_MarksAnalysisStale_AndRestoringItRecovers()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        viewModel.ChapterText += " Extra.";
        Assert.True(viewModel.IsAnalysisStale);
        Assert.False(viewModel.HasAnalysis);
        Assert.Empty(viewModel.Segments);

        viewModel.ChapterText = Chapter1;
        Assert.False(viewModel.IsAnalysisStale);
        Assert.True(viewModel.HasAnalysis);
        Assert.NotEmpty(viewModel.Segments);
    }

    [Fact]
    public async Task SpeechReview_AppliesOnlyAcceptedEditsAndPersistsWithoutChangingSource()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new CombinedEditProvider());
        var sourceText = viewModel.ChapterText;

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        var edit = Assert.Single(viewModel.SpeechEdits);
        Assert.Equal(SpeechEditStatus.Pending, edit.Status);
        Assert.Equal(sourceText, viewModel.ChapterText);
        Assert.DoesNotContain("in agreement", viewModel.Segments.Select(segment => segment.Text).ToArray());

        var acceptCommand = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(edit.AcceptCommand);
        await acceptCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsAnalysisStale);
        Assert.True(viewModel.HasAnalysis);
        Assert.Contains(viewModel.Segments, segment => segment.Text.Contains("Tom nodded in agreement.", StringComparison.Ordinal));
        Assert.Equal(sourceText, viewModel.ChapterText);

        Assert.True(viewModel.HasAnalysis);
        Assert.Contains(viewModel.Segments, segment => segment.Text.Contains("Tom nodded in agreement.", StringComparison.Ordinal));
        Assert.Equal(sourceText, viewModel.ChapterText);
        var projectId = Assert.Single(viewModel.RecentProjects).Id;
        Assert.Equal(
            SpeechEditStatus.Accepted,
            Assert.Single((await store.LoadSpeechReviewAsync(projectId, "c1"))!.Suggestions).Status);

        var reopened = CreateViewModel(store, new MockLlmProvider());
        await reopened.InitializeAsync();
        await WaitUntilAsync(() => reopened.SpeechEdits.Count == 1);
        Assert.Equal(SpeechEditStatus.Accepted, Assert.Single(reopened.SpeechEdits).Status);
        Assert.Contains(reopened.Segments, segment => segment.Text.Contains("Tom nodded in agreement.", StringComparison.Ordinal));
        Assert.Equal(sourceText, reopened.ChapterText);
    }

    [Fact]
    public async Task Cancel_StopsAnalysis_AndKeepsPreviousStateUsable()
    {
        var provider = new BlockingProvider();
        var (viewModel, _) = await CreateImportedViewModelAsync(provider);

        var running = viewModel.AnalyzeChapterCommand.ExecuteAsync(null);
        await provider.Started.Task;
        Assert.True(viewModel.IsAnalyzing);
        Assert.False(viewModel.AnalyzeChapterCommand.CanExecute(null));
        viewModel.AnalyzeChapterCancelCommand.Execute(null);
        await running;

        Assert.False(viewModel.IsAnalyzing);
        Assert.Equal("Analysis cancelled.", viewModel.AnalysisStatus);
        Assert.False(viewModel.HasAnalysis);
        Assert.True(viewModel.AnalyzeChapterCommand.CanExecute(null));
    }

    [Fact]
    public async Task SingleNarratorMode_ProducesNarrationOnlyWithoutCharacters()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new MockLlmProvider());
        viewModel.UseMultipleVoices = false;

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasAnalysis);
        Assert.Empty(viewModel.Characters);
        Assert.All(viewModel.Segments, segment => Assert.True(segment.IsNarration));
    }

    [Fact]
    public async Task DeleteProject_RequiresConfirmationAndClearsOpenWorkspace()
    {
        var (viewModel, store) = await CreateImportedViewModelAsync(new MockLlmProvider());
        var projectId = Assert.Single(viewModel.RecentProjects).Id;
        var sourcePath = Path.Combine(_directory, "book.epub");

        viewModel.DeleteRecentProjectCommand.Execute(null);

        Assert.True(viewModel.IsDeleteConfirmationVisible);
        Assert.NotNull(viewModel.Book);
        viewModel.CancelDeleteProjectCommand.Execute(null);
        Assert.False(viewModel.IsDeleteConfirmationVisible);

        viewModel.DeleteRecentProjectCommand.Execute(null);
        await viewModel.ConfirmDeleteProjectCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeleteConfirmationVisible);
        Assert.Null(viewModel.Book);
        Assert.Empty(viewModel.Chapters);
        Assert.Empty(viewModel.RecentProjects);
        Assert.Null(await store.LoadAsync(projectId));
        Assert.True(File.Exists(sourcePath));
        Assert.Contains("Deleted project", viewModel.StatusMessage);
    }

    [Fact]
    public async Task WorkerFailure_IsReportedInStatus()
    {
        var (viewModel, _) = await CreateImportedViewModelAsync(new FailingProvider());

        await viewModel.AnalyzeChapterCommand.ExecuteAsync(null);

        Assert.StartsWith("Analysis failed:", viewModel.AnalysisStatus);
        Assert.False(viewModel.IsAnalyzing);
    }

    private async Task<(MainWindowViewModel ViewModel, SqliteProjectStore Store)> CreateImportedViewModelAsync(
        ILlmProvider provider,
        string? exportPath = null,
        FakeAudioPlaybackService? audioPlayback = null,
        IThemePreferenceService? themePreferences = null,
        ITtsProvider? ttsProvider = null)
    {
        var store = new SqliteProjectStore(Path.Combine(_directory, "projects.sqlite3"));
        var viewModel = CreateViewModel(
            store,
            provider,
            exportPath,
            audioPlayback,
            themePreferences: themePreferences,
            ttsProvider: ttsProvider);
        await viewModel.InitializeAsync();
        await viewModel.ImportEpubCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.SelectedChapter);
        viewModel.UseMultipleVoices = true;
        return (viewModel, store);
    }

    private MainWindowViewModel CreateViewModel(
        SqliteProjectStore store,
        ILlmProvider provider,
        string? exportPath = null,
        FakeAudioPlaybackService? audioPlayback = null,
        string? m4bExportPath = null,
        IM4bExporter? m4bExporter = null,
        IThemePreferenceService? themePreferences = null,
        ITtsProvider? ttsProvider = null)
    {
        var epubPath = Path.Combine(_directory, "book.epub");
        File.WriteAllBytes(epubPath, [0]);
        var worker = new ProtocolNarrativeWorker(
            new LocalWorkerHost(new ChapterAnalyzer(provider)),
            provider.Capabilities);
        var speechProvider = ttsProvider ?? new MockTtsProvider();
        var audioGeneration = new AudioGenerationService(store, speechProvider);
        return new MainWindowViewModel(
            new FakeImporter(),
            store,
            new FakePicker(epubPath, exportPath, m4bExportPath),
            worker,
            store,
            store,
            speechProvider,
            audioGeneration,
            new BookAudioGenerationService(worker, store, audioGeneration),
            m4bExporter ?? new RecordingM4bExporter(),
            new BookStructureAnalyzer(new FakeStructureProvider()),
            audioPlayback ?? new FakeAudioPlaybackService(),
            themePreferences);
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeKokoroSpeechEngineFactory : IKokoroSpeechEngineFactory
    {
        public IKokoroSpeechEngine Create(KokoroModelFiles modelFiles, string language) =>
            throw new NotSupportedException("The speech engine should not be created before downloading the model.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(await condition());
    }

    private sealed class FakePicker(string path, string? exportPath, string? m4bExportPath) : IFilePickerService
    {
        public Task<string?> PickEpubAsync() => Task.FromResult<string?>(path);

        public Task<string?> PickWavSavePathAsync(string chapterTitle) =>
            Task.FromResult(exportPath);

        public Task<string?> PickM4bSavePathAsync(string bookTitle) =>
            Task.FromResult(m4bExportPath);
    }

    private sealed class RecordingM4bExporter : IM4bExporter
    {
        public GeneratedBookAudio? LastBook { get; private set; }

        public async Task ExportAsync(
            GeneratedBookAudio book,
            string outputPath,
            CancellationToken cancellationToken)
        {
            LastBook = book;
            await File.WriteAllTextAsync(outputPath, "M4B", cancellationToken);
        }
    }

    private sealed class FakeAudioPlaybackService : IAudioPlaybackService
    {
        public bool IsPlaying { get; private set; }

        public bool IsPaused { get; private set; }

        public TimeSpan Position { get; private set; }

        public TimeSpan Duration { get; private set; }

        public void Play(byte[] wavBytes)
        {
            LastPlayedAudio = wavBytes;
            Duration = TimeSpan.FromSeconds(1);
            Position = TimeSpan.Zero;
            IsPlaying = true;
            IsPaused = false;
        }

        public byte[]? LastPlayedAudio { get; private set; }

        public void Pause()
        {
            IsPlaying = false;
            IsPaused = true;
        }

        public void Resume()
        {
            IsPlaying = true;
            IsPaused = false;
        }

        public void Stop()
        {
            IsPlaying = false;
            IsPaused = false;
            Position = TimeSpan.Zero;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingSecondSegmentTtsProvider : ITtsProvider
    {
        private readonly MockTtsProvider _inner = new();
        private int _calls;
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderCapabilities Capabilities => _inner.Capabilities;
        public IReadOnlyList<string> VoiceIds => _inner.VoiceIds;
        public IReadOnlyList<string> Languages => _inner.Languages;

        public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                SecondStarted.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return await _inner.SynthesizeAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingTtsProvider : ITtsProvider
    {
        private readonly MockTtsProvider _inner = new();
        private readonly bool _includeNativeLanguageMetadata;
        private readonly bool _includeSpanishNativeMatch;

        public RecordingTtsProvider(
            bool includeNativeLanguageMetadata = true,
            bool includeSpanishNativeMatch = true)
        {
            _includeNativeLanguageMetadata = includeNativeLanguageMetadata;
            _includeSpanishNativeMatch = includeSpanishNativeMatch;
        }

        public ProviderCapabilities Capabilities => _inner.Capabilities;

        public IReadOnlyList<string> VoiceIds => _inner.VoiceIds;

        public IReadOnlyList<string> Languages { get; } = ["English", "Spanish"];

        public bool SupportsVoiceInstructions => true;

        public string? GetVoiceNativeLanguage(string voiceId) =>
            !_includeNativeLanguageMetadata
                ? null
                : voiceId switch
                {
                    "mock-warm" => "English",
                    "mock-calm" when _includeSpanishNativeMatch => "Spanish",
                    "mock-bright" => "English",
                    "mock-calm" => null,
                    _ => null
                };

        public SpeechRequest? LastRequest { get; private set; }

        public Task<SpeechResult> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _inner.SynthesizeAsync(request with { Language = "English" }, cancellationToken);
        }
    }

    private sealed class FakeStructureProvider : IBookStructureProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("fake structure provider", true, 4000);

        public Task<BookStructureResult> AnalyzeAsync(
            BookStructureRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookStructureResult(
                "Test genre",
                request.Chapters.Select(chapter => new ChapterStructureSuggestion(
                    chapter.Id,
                    chapter.CurrentRole,
                    0.95,
                    "Test rationale.")).ToArray()));
    }

    private sealed class RecordingVoiceDesignProvider : ITtsProvider, IVoiceDesignProvider
    {
        private readonly byte[] _wavBytes = WavPcm.CreateSilence(TimeSpan.FromMilliseconds(250));

        public ProviderCapabilities Capabilities { get; } =
            new("Qwen3-TTS VoiceDesign test", IsMock: false, MaxContextCharacters: 700);

        public IReadOnlyList<string> VoiceIds { get; } = [QwenTtsProvider.DesignedVoiceId];

        public IReadOnlyList<string> Languages { get; } = ["English", "Spanish"];

        public bool SupportsVoiceInstructions => true;

        public bool SupportsVoiceDesign => true;

        public VoiceDesignRequest? LastDesignRequest { get; private set; }

        public SpeechRequest? LastRequest { get; private set; }

        public Task<SpeechResult> GenerateVoiceDesignAsync(
            VoiceDesignRequest request,
            CancellationToken cancellationToken)
        {
            LastDesignRequest = request;
            return Task.FromResult(new SpeechResult(_wavBytes, TimeSpan.FromMilliseconds(250)));
        }

        public Task<SpeechResult> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new SpeechResult(_wavBytes, TimeSpan.FromMilliseconds(250)));
        }
    }

    private sealed class CombinedEditProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("fake combined provider", true, 4000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            var target = "Tom nodded.";
            var start = request.Text.IndexOf(target, StringComparison.Ordinal);
            IReadOnlyList<SpeechEditProposal> edits = start >= 0
                ? [new SpeechEditProposal(target, "Tom nodded in agreement.", "The added phrase reads naturally aloud.")]
                : [];
            return Task.FromResult(new SegmentationResult(
                [],
                [new NarrativeSegment(0, SegmentKind.Narration, null, 0, request.Text.Length)],
                edits));
        }
    }

    private sealed class FakeImporter : IBookImporter
    {
        public ImportedBook Import(Stream stream) =>
            new("Test Book", [new ImportedChapter("c1", "Chapter One", Chapter1)]);
    }

    private sealed class BlockingProvider : ILlmProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities Capabilities { get; } = new("blocking", true, 1000);

        public async Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FailingProvider : ILlmProvider
    {
        public ProviderCapabilities Capabilities { get; } = new("failing", true, 1000);

        public Task<SegmentationResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("model crashed");
    }
}
