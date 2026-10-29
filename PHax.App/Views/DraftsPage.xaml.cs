using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class DraftsPage : Page
{
    public DraftsViewModel ViewModel { get; } = App.GetService<DraftsViewModel>();

    public DraftsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } d) ComposeWindow.Open(d.Id);
    }

    private void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Edit_Click(sender, e);

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } d) return;
        var m = d.Model;
        try
        {
            var paths = App.GetService<AppPaths>();
            await App.GetService<FaxDocumentService>().SaveAsync(new ComposeData
            {
                DraftId = m.Id, RecipientName = m.RecipientName, FaxNumber = m.FaxNumber, Subject = m.Subject,
                IncludeCoverPage = m.IncludeCoverPage, CoverMessage = m.CoverMessage,
                Attachments = m.Attachments.OrderBy(a => a.Order).Select(a => new ComposeAttachment
                {
                    ExistingId = a.Id, FileName = a.OriginalFileName, Path = paths.Resolve(a.StoredPath), PageCount = a.PageCount,
                }).ToList(),
            }, queue: true);
            await ViewModel.RefreshAsync();
            App.Shell?.NavigateTo("outbox");
        }
        catch (Exception ex)
        {
            await Dialogs.MessageAsync(XamlRoot, "Send draft", ex.Message);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete draft", $"Delete the draft to {ViewModel.Selected.Title}?", "Delete")) return;
        await ViewModel.DeleteSelectedAsync();
    }
}
