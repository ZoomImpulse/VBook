using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using VBook.App.ViewModels;
using VBook.Core.Books;

namespace VBook.App;

public partial class App : Application
{
    private readonly ServiceProvider _services;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddSingleton<IBookImporter, EpubImporter>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
        _services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = _services.GetRequiredService<MainWindow>();
        window.Activate();
    }
}
