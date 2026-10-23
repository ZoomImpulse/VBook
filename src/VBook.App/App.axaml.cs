using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VBook.App.Services;
using VBook.App.ViewModels;
using VBook.App.Views;
using VBook.Core.Books;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();
            var window = _services.GetRequiredService<MainWindow>();
            _services.GetRequiredService<AvaloniaFilePickerService>().Attach(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBookImporter, EpubImporter>();
        services.AddSingleton(_ =>
        {
            var projectDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAudiobookStudio");
            return new SqliteProjectStore(Path.Combine(projectDirectory, "projects.sqlite3"));
        });
        services.AddSingleton<IProjectStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
        services.AddSingleton<INarrativeStore>(sp => sp.GetRequiredService<SqliteProjectStore>());
        services.AddSingleton<ILlmProvider, MockLlmProvider>();
        services.AddSingleton<ChapterAnalyzer>();
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
