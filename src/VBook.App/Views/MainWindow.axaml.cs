using Avalonia.Controls;
using VBook.App.ViewModels;

namespace VBook.App.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is null || _closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        // Persist pending edits before the window goes away.
        e.Cancel = true;
        try
        {
            await _viewModel.FlushAsync();
        }
        catch (Exception)
        {
        }

        _closeConfirmed = true;
        Close();
    }
}
