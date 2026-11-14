using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using VBook.App.Services;
using VBook.App.ViewModels;
using VBook.App.Views;
using VBook.Core.Books;
using VBook.Core.Generation;
using VBook.Core.Narrative;
using VBook.Core.Projects;
using VBook.Core.Providers;
using VBook.Core.Worker;

namespace VBook.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _services = ConfigureServices();
        _services.GetRequiredService<IThemePreferenceService>();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = _services.GetRequiredService<MainWindow>();
            _services.GetRequiredService<AvaloniaFilePickerService>().Attach(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IThemePreferenceService>(_ =>
            new AvaloniaThemePreferenceService(preference =>
            {
                RequestedThemeVariant = preference switch
                {
                    AppThemePreference.Light => ThemeVariant.Light,
                    AppThemePreference.Dark => ThemeVariant.Dark,
                    _ => ThemeVariant.Default
                };
            }));
        services.AddSingleton<IBookImporter, EpubImporter>();
        var projectDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAudiobookStudio");
        services.AddSingleton(_ =>
        {
            return new SqliteProjectStore(Path.Combine(projectDirectory, "projects.sqlite3"));
        });
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
        services.AddSingleton<INarrativeStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
        services.AddSingleton<IGenerationStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        var endpoint = Environment.GetEnvironmentVariable("VBOOK_LLM_ENDPOINT")
            ?? "http://localhost:11434/v1/chat/completions";
        var model = Environment.GetEnvironmentVariable("VBOOK_LLM_MODEL") ?? "frob/qwen3.5-instruct:9b";
        services.AddSingleton(sp => new OpenAiCompatibleLlmProvider(
            sp.GetRequiredService<HttpClient>(),
            new Uri(endpoint),
            model));
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<OpenAiCompatibleLlmProvider>());
        services.AddSingleton<IBookStructureProvider>(sp => sp.GetRequiredService<OpenAiCompatibleLlmProvider>());
        var piperExecutable = Environment.GetEnvironmentVariable("VBOOK_PIPER_EXECUTABLE");
        var piperVoicesDirectory = Environment.GetEnvironmentVariable("VBOOK_PIPER_VOICES_DIRECTORY");
        services.AddSingleton(sp => new KokoroModelAssetManager(
            sp.GetRequiredService<HttpClient>(),
            Path.Combine(projectDirectory, "models")));
        services.AddSingleton(sp => new QwenTtsModelAssetManager(
            sp.GetRequiredService<HttpClient>(),
            Path.Combine(projectDirectory, "models")));
        services.AddSingleton(sp => new QwenTtsSetupService(
            sp.GetRequiredService<QwenTtsModelAssetManager>(),
            projectDirectory,
            Path.Combine(AppContext.BaseDirectory, "Services", "qwen_tts_worker.py")));
        services.AddSingleton<IKokoroSpeechEngineFactory, KokoroSpeechEngineFactory>();
        services.AddSingleton(sp => new LocalTtsProviderService(
            sp.GetRequiredService<KokoroModelAssetManager>(),
            sp.GetRequiredService<IKokoroSpeechEngineFactory>(),
            piperExecutable,
            piperVoicesDirectory,
            qwenSetup: sp.GetRequiredService<QwenTtsSetupService>()));
        services.AddSingleton<ITtsProvider>(sp => sp.GetRequiredService<LocalTtsProviderService>());

        services.AddSingleton<IAudioPlaybackService, OpenAlAudioPlaybackService>();
        services.AddSingleton<ChapterAnalyzer>();
        services.AddSingleton<BookStructureAnalyzer>();
        services.AddSingleton<AudioGenerationService>();
        services.AddSingleton<BookAudioGenerationService>();
        services.AddSingleton<IM4bExporter>(_ => new FfmpegM4bExporter(
            Environment.GetEnvironmentVariable("VBOOK_FFMPEG_EXECUTABLE") ?? "ffmpeg"));
        services.AddSingleton<IWorkerTransport, LocalWorkerHost>();
        services.AddSingleton<INarrativeWorker>(sp => new ProtocolNarrativeWorker(
            sp.GetRequiredService<IWorkerTransport>(),
            sp.GetRequiredService<ILlmProvider>().Capabilities));
        services.AddSingleton<AvaloniaFilePickerService>();
        services.AddSingleton<IFilePickerService>(sp => sp.GetRequiredService<AvaloniaFilePickerService>());
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
