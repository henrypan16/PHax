using PHax.App.Services;
using PHax.App.ViewModels;
using PHax.Core.Ipc;
using PHax.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

public sealed partial class OutboxPage : Page
{
    public OutboxViewModel ViewModel { get; } = App.GetService<OutboxViewModel>();

    public OutboxPage()
    {
        InitializeComponent();
        Pane.AttachToggle(PaneToggle);
        Pane.AddShortcutsTo(this);
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = ((ListView)sender).SelectedItem as OutboundItem;
        if (item is null) Pane.Clear();
        else if (item.Model.DocumentPath is null) Pane.Clear("This fax has no document (it could not be prepared).");
        else Pane.ShowFax(item.Title, item.Model);
    }

    private async Task Run(string command)
    {
        var message = await ViewModel.CommandAsync(command);
        if (!string.IsNullOrEmpty(message) && message is not ("Cancelled." or "Paused." or "Resumed." or "Queued for retry."))
            await Dialogs.MessageAsync(XamlRoot, "Outbox", message);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected?.IsActive == true &&
            !await Dialogs.ConfirmAsync(XamlRoot, "Cancel transmission", "The fax is being transmitted. Pages already sent may have been received. Cancel anyway?", "Cancel fax"))
            return;
        await Run(PipeCommands.CancelJob);
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await Run(PipeCommands.PauseJob);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await Run(PipeCommands.ResumeJob);

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected?.Model.Status == OutboundStatus.Uncertain &&
            !await Dialogs.ConfirmAsync(XamlRoot, "Retry unconfirmed fax",
                "The receiver did not confirm this fax, but it may have received it. Retrying could deliver a duplicate. Retry the whole fax?", "Retry"))
            return;
        await Run(PipeCommands.RetryJob);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { Model.DocumentPath: not null } item) return;
        try { PreviewWindow.Open(item.Title, await App.GetService<IFaxData>().LoadOutboundPagesAsync(item.Model)); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Preview", ex.Message); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete", $"Delete the fax to {ViewModel.Selected.Title}?", "Delete")) return;
        try { await ViewModel.DeleteSelectedAsync(); }
        catch (Exception ex) { await Dialogs.MessageAsync(XamlRoot, "Delete", ex.Message); }
    }
}
