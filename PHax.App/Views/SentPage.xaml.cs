using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class SentPage : Page
{
    public SentViewModel ViewModel { get; } = App.GetService<SentViewModel>();

    public SentPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async Task<List<FaxBitmap>?> PagesAsync()
    {
        if (ViewModel.Selected?.Model.DocumentPath is not { } path) return null;
        try { return await Task.Run(() => App.GetService<FaxDocumentService>().LoadPages(path)); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Document", ex.Message); return null; }
    }

    private string FaxName => $"Fax to {ViewModel.Selected?.Title} {ViewModel.Selected?.Model.SentUtc?.ToLocalTime():yyyy-MM-dd HHmm}";

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (await PagesAsync() is { } pages) PreviewWindow.Open(FaxName, pages);
    }

    private void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Preview_Click(sender, e);

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (await PagesAsync() is { } pages) await FaxActions.PrintAsync(XamlRoot, pages, FaxName);
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (await PagesAsync() is { } pages) await FaxActions.SavePdfAsync(App.Shell, XamlRoot, pages, FaxName);
    }

    private async void ExportTiff_Click(object sender, RoutedEventArgs e)
    {
        if (await PagesAsync() is { } pages) await FaxActions.SaveTiffAsync(App.Shell, pages, FaxName);
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Resend", $"Send this fax to {ViewModel.Selected.Title} again?", "Resend")) return;
        try
        {
            await ViewModel.ResendAsync();
            App.GetService<ShellViewModel>().RaiseOutboundChanged();
            App.Shell?.NavigateTo("outbox");
        }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Resend", ex.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete", "Delete this sent fax and its document?", "Delete")) return;
        await ViewModel.DeleteSelectedAsync();
    }
}
