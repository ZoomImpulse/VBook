using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VBook.App.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VBook.App;

public sealed partial class MainWindow : Window
{
    private bool _updatingEditor;

    public MainWindow(MainWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Local Audiobook Studio";
        StatusText.DataContext = viewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ChapterList.SelectedItem = ViewModel.SelectedChapter;
        UpdateSelectedChapter();
    }

    public MainWindowViewModel ViewModel { get; }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".epub");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        ImportButton.IsEnabled = false;
        try
        {
            await ViewModel.ImportEpubAsync(file.Path);
            BookTitleText.Text = ViewModel.Book?.Title ?? "Local Audiobook Studio";
            ChapterList.SelectedItem = ViewModel.SelectedChapter;
            UpdateSelectedChapter();
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedChapter = ChapterList.SelectedItem as VBook.Core.Books.ImportedChapter;
        UpdateSelectedChapter();
    }

    private void ChapterEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingEditor && ChapterEditor.IsEnabled)
        {
            ViewModel.UpdateSelectedText(ChapterEditor.Text);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StatusMessage))
        {
            StatusText.Text = ViewModel.StatusMessage;
        }
    }

    private void UpdateSelectedChapter()
    {
        _updatingEditor = true;
        var chapter = ViewModel.SelectedChapter;
        ChapterHeading.Text = chapter?.Title ?? "No chapter selected";
        ChapterEditor.IsEnabled = chapter is not null;
        ChapterEditor.Text = chapter is null ? string.Empty : ViewModel.GetChapterText(chapter);
        _updatingEditor = false;
    }
}
