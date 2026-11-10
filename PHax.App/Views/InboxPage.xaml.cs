using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Imaging.Encoding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class InboxPage : Page
{
    public InboxViewModel ViewModel { get; } = App.GetService<InboxViewModel>();

    public InboxPage()
    {
        InitializeComponent();
        Pane.AttachToggle(PaneToggle, Body);
        Pane.AddShortcutsTo(this);
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = ((ListView)sender).SelectedItem as ReceivedItem;
        if (item is null) { Pane.Clear(); return; }
        if (item.Model.TiffPath is null) { Pane.Clear("This fax has no document (the reception did not complete)."); return; }
        Pane.ShowFax(item.Title, item.Model);
        // Shown in the reading pane = read, as in an email client.
        if (item.IsUnread) await MarkAsync(item, true);
    }

    private async Task<List<FaxBitmap>?> PagesAsync()
    {
        if (ViewModel.Selected is not { Model.TiffPath: not null } item) return null;
        try { return [.. await App.GetService<IFaxData>().LoadReceivedPagesAsync(item.Model)]; }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Document", ex.Message); return null; }
    }

    private async Task MarkAsync(ReceivedItem item, bool read)
    {
        // On a client PC this can fail if the server just went away; reading the fax must not turn into an error.
        try { await ViewModel.SetReadAsync(item, read); }
        catch (FaxServerException ex) { Serilog.Log.Warning("Could not update the fax: {Message}", ex.Message); }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item || await PagesAsync() is not { } pages) return;
        PreviewWindow.Open(item.Title, pages);
        if (item.IsUnread) await MarkAsync(item, true);
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

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item || await PagesAsync() is not { } pages) return;
        // The received fax becomes an attachment of a new fax; the original stays in the Inbox.
        var copy = Path.Combine(Path.GetTempPath(), $"{FaxActions.Sanitize(item.Title)}.tif");
        await Task.Run(() => TiffFax.Write(copy, pages));
        ComposeWindow.Open(null, [copy]);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item) return;
        var name = await Dialogs.PromptAsync(XamlRoot, "Rename fax", item.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await ViewModel.RenameAsync(item, name); }
        catch (FaxServerException ex) { await Dialogs.MessageAsync(XamlRoot, "Rename fax", ex.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } item) return;
        var message = App.GetService<ShellViewModel>().IsClient
            ? $"Delete \"{item.Title}\" for everyone? The Inbox is shared with the other PCs."
            : $"Delete \"{item.Title}\" and its files?";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete fax", message, "Delete")) return;
        try { await ViewModel.DeleteAsync(item); }
        catch (FaxServerException ex) { await Dialogs.MessageAsync(XamlRoot, "Delete fax", ex.Message); }
    }

    private async void MarkUnread_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item) await MarkAsync(item, false);
    }

    private async void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } item) await MarkAsync(item, true);
    }
}
