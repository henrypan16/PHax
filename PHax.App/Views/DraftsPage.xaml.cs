using System.ComponentModel;
using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Models;
using PHax.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class DraftsPage : Page
{
    private OutboundItem? _previewed;

    public DraftsViewModel ViewModel { get; } = App.GetService<DraftsViewModel>();

    public DraftsPage()
    {
        InitializeComponent();
        Pane.AttachToggle(PaneToggle, Body);
        Pane.AddShortcutsTo(this);
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
        Unloaded += (_, _) => Watch(null);
    }

    private static ComposeData ToComposeData(OutboundFax m)
    {
        var paths = App.GetService<AppPaths>();
        return new ComposeData
        {
            DraftId = m.Id, RecipientName = m.RecipientName, FaxNumber = m.FaxNumber, Subject = m.Subject,
            IncludeCoverPage = m.IncludeCoverPage, CoverMessage = m.CoverMessage,
            Attachments = m.Attachments.OrderBy(a => a.Order).Select(a => new ComposeAttachment
            {
                ExistingId = a.Id, FileName = a.OriginalFileName, Path = paths.Resolve(a.StoredPath), PageCount = a.PageCount,
            }).ToList(),
        };
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Watch(((ListView)sender).SelectedItem as OutboundItem);
        ShowPreview();
    }

    /// <summary>Drafts are updated in place when edited in the compose window; re-render the selected one when it changes.</summary>
    private void Watch(OutboundItem? item)
    {
        if (_previewed is not null) _previewed.PropertyChanged -= OnPreviewedChanged;
        _previewed = item;
        if (item is not null) item.PropertyChanged += OnPreviewedChanged;
    }

    private void OnPreviewedChanged(object? sender, PropertyChangedEventArgs e) => ShowPreview();

    private void ShowPreview()
    {
        if (_previewed is not { } item) { Pane.Clear(); return; }
        var m = item.Model;
        if (m.Attachments.Count == 0 && !m.IncludeCoverPage) { Pane.Clear("This draft has no attachments or cover page yet."); return; }
        var data = ToComposeData(m);
        // The key changes with every save, so an edited draft is rendered again.
        Pane.Show($"draft:{m.Id}:{m.UpdatedUtc.Ticks}", item.Title,
            async ct => await App.GetService<FaxDocumentService>().RenderPreviewAsync(data, ct: ct));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is { } d) ComposeWindow.Open(d.Id);
    }

    private void List_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Edit_Click(sender, e);

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } d) return;
        try
        {
            await App.GetService<FaxDocumentService>().SaveAsync(ToComposeData(d.Model), queue: true);
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
