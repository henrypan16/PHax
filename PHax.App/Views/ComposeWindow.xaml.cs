using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Models;
using PHax.Imaging.Conversion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PHax.App.Views;

public sealed partial class ComposeWindow : Window
{
    private CancellationTokenSource? _previewCts;

    public ComposeViewModel ViewModel { get; } = App.GetService<ComposeViewModel>();

    private ComposeWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 860));
        AppIcon.Apply(AppWindow);
    }

    public static async void Open(Guid? draftId = null, IEnumerable<string>? files = null)
    {
        var window = new ComposeWindow();
        if (draftId is not null) window.Title = "Edit draft";
        window.Activate();
        await window.ViewModel.InitializeAsync(draftId, files);
    }

    private void Recipient_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = sender.Text.Trim();
        sender.ItemsSource = text.Length == 0 ? null : ViewModel.RecentRecipients
            .Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.FaxNumber.Contains(text)).Take(10).ToList();
    }

    private void Recipient_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is Recipient r)
        {
            ViewModel.RecipientName = r.Name;
            ViewModel.FaxNumber = r.FaxNumber;
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in await Pickers.OpenFilesAsync(this, DocumentRasterizer.SupportedExtensions)) await ViewModel.AddFileAsync(f);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => ViewModel.Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => ViewModel.Move(1);
    private void Remove_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveSelected();

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void List_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        foreach (var item in await e.DataView.GetStorageItemsAsync())
            if (item is StorageFile file) await ViewModel.AddFileAsync(file.Path);
    }

    private async void Attachments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var item = ViewModel.SelectedAttachment;
        PreviewImage.Source = null;
        PreviewHint.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        if (item is null) return;
        PreviewRing.IsActive = true;
        try
        {
            var pages = await Task.Run(() => new DocumentRasterizer().RasterizeAsync(item.Source.Path, cts.Token));
            if (cts.IsCancellationRequested || pages.Count == 0) return;
            PreviewImage.Source = await FaxActions.ToImageAsync(pages[0]);
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            PreviewHint.Text = $"Preview failed: {ex.Message}";
            PreviewHint.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!cts.IsCancellationRequested) PreviewRing.IsActive = false;
        }
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.SaveAsync(send: false)) Close();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.SaveAsync(send: true))
        {
            App.Shell?.NavigateTo("outbox");
            Close();
        }
    }
}
