using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Models;
using PHax.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class InboxPage : Page
{
    public InboxViewModel ViewModel { get; } = App.GetService<InboxViewModel>();

    public InboxPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async Task<List<FaxBitmap>?> PagesAsync()
    {
        if (ViewModel.Selected?.Model.TiffPath is not { } path) return null;
        try { return await Task.Run(() => App.GetService<FaxDocumentService>().LoadPages(path)); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Document", ex.Message); return null; }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item || await PagesAsync() is not { } pages) return;
        PreviewWindow.Open(item.Title, pages);
        if (item.IsUnread) await ViewModel.SetReadAsync(item, true);
    }

    private void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Preview_Click(sender, e);

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item && await PagesAsync() is { } pages) await FaxActions.PrintAsync(XamlRoot, pages, item.Title);
    }

    private async void SavePdf_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item && await PagesAsync() is { } pages) await FaxActions.SavePdfAsync(App.Shell, XamlRoot, pages, item.Title);
    }

    private async void SaveTiff_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item && await PagesAsync() is { } pages) await FaxActions.SaveTiffAsync(App.Shell, pages, item.Title);
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected?.Model.TiffPath is not { } path) return;
        // The received TIFF becomes an attachment of a new fax; the original stays in the Inbox.
        var copy = Path.Combine(Path.GetTempPath(), $"{FaxActions.Sanitize(ViewModel.Selected.Title)}.tif");
        File.Copy(App.GetService<AppPaths>().Resolve(path), copy, overwrite: true);
        ComposeWindow.Open(null, [copy]);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item) return;
        var name = await Dialogs.PromptAsync(XamlRoot, "Rename fax", item.Title);
        if (!string.IsNullOrWhiteSpace(name)) await ViewModel.RenameAsync(item, name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item) return;
        if (await Dialogs.ConfirmAsync(XamlRoot, "Delete fax", $"Delete \"{item.Title}\" and its files?", "Delete")) await ViewModel.DeleteAsync(item);
    }

    private async void MarkUnread_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item) await ViewModel.SetReadAsync(item, false);
    }

    private async void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item) await ViewModel.SetReadAsync(item, true);
    }
}
