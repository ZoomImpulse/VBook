using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VBook.App.Services;
using VBook.App.ViewModels;
using VBook.App.Views;
using VBook.Core.Books;
using VBook.Core.Projects;

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
        services.AddSingleton<IProjectStore>(_ =>
        {
            var projectDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAudiobookStudio");
            return new SqliteProjectStore(Path.Combine(projectDirectory, "projects.sqlite3"));
        });
        services.AddSingleton<AvaloniaFilePickerService>();
        services.AddSingleton<IFilePickerService>(sp => sp.GetRequiredService<AvaloniaFilePickerService>());
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
