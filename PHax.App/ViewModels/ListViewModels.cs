using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Core.Remote;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.ViewModels;

/// <summary>Base for the Drafts / Outbox / Sent lists, which are filtered views of outbound jobs.</summary>
public abstract partial class OutboundListViewModel : ObservableObject
{
    protected readonly IServiceConnection Client;

    public ObservableCollection<OutboundItem> Items { get; } = [];
    [ObservableProperty] public partial OutboundItem? Selected { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    protected OutboundListViewModel(IServiceConnection client, ShellViewModel shell)
    {
        Client = client;
        shell.OutboundChanged += () => _ = RefreshAsync();
        shell.JobProgress += p => Items.FirstOrDefault(i => i.Id == p.JobId)?.Apply(p);
    }

    protected abstract Task<List<OutboundFax>> LoadAsync(string? search);
    protected abstract Task DeleteAsync(Guid id);

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        List<OutboundFax> rows;
        try { rows = await LoadAsync(Search); }
        catch (FaxServerException ex)
        {
            // The banner reports the lost connection; the list keeps showing what it last knew.
            Serilog.Log.Warning("Could not refresh faxes: {Message}", ex.Message);
            return;
        }
        var selectedId = Selected?.Id;
        // Update in place to keep selection and progress.
        for (int i = Items.Count - 1; i >= 0; i--)
            if (rows.All(r => r.Id != Items[i].Id)) Items.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            var existing = Items.FirstOrDefault(x => x.Id == rows[i].Id);
            if (existing is null) Items.Insert(Math.Min(i, Items.Count), new OutboundItem(rows[i]));
            else
            {
                existing.Update(rows[i]);
                int at = Items.IndexOf(existing);
                if (at != i) Items.Move(at, Math.Min(i, Items.Count - 1));
            }
        }
        IsEmpty = Items.Count == 0;
        if (selectedId is not null) Selected = Items.FirstOrDefault(x => x.Id == selectedId);
    }

    public async Task<string?> CommandAsync(string command)
    {
        if (Selected is null) return null;
        if (!Client.IsConnected) return Client.UnavailableMessage;
        try { return await Client.RequestAsync<string>(command, new JobRef(Selected.Id)); }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is null) return;
        await DeleteAsync(Selected.Id);
        await RefreshAsync();
    }
}

/// <summary>Drafts always stay on this PC, in client mode too: they reach the server only when sent.</summary>
public sealed class DraftsViewModel(IDbContextFactory<FaxDbContext> db, FaxRecords records, IServiceConnection client, ShellViewModel shell)
    : OutboundListViewModel(client, shell)
{
    protected override async Task<List<OutboundFax>> LoadAsync(string? search)
    {
        await using var ctx = await db.CreateDbContextAsync();
        var q = ctx.OutboundFaxes.AsNoTracking().Include(f => f.Attachments).Where(f => f.Status == OutboundStatus.Draft);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(f => f.RecipientName.Contains(s) || f.FaxNumber.Contains(s) || f.Subject.Contains(s));
        }
        return await q.OrderByDescending(f => f.UpdatedUtc).Take(500).ToListAsync();
    }

    protected override Task DeleteAsync(Guid id) => records.DeleteOutboundAsync(id);
}

public sealed class OutboxViewModel(IFaxData data, IServiceConnection client, ShellViewModel shell)
    : OutboundListViewModel(client, shell)
{
    protected override Task<List<OutboundFax>> LoadAsync(string? search) => data.ListOutboundAsync(OutboundView.Outbox, search);
    protected override Task DeleteAsync(Guid id) => data.DeleteOutboundAsync(id);
}

public sealed class SentViewModel(IFaxData data, IServiceConnection client, ShellViewModel shell)
    : OutboundListViewModel(client, shell)
{
    protected override Task<List<OutboundFax>> LoadAsync(string? search) => data.ListOutboundAsync(OutboundView.Sent, search);
    protected override Task DeleteAsync(Guid id) => data.DeleteOutboundAsync(id);

    public Task ResendAsync() => Selected is null ? Task.CompletedTask : data.ResendAsync(Selected.Id);
}

public sealed partial class InboxViewModel : ObservableObject
{
    private readonly IFaxData _data;
    private readonly ShellViewModel _shell;

    public ObservableCollection<ReceivedItem> Items { get; } = [];
    [ObservableProperty] public partial ReceivedItem? Selected { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>When true, lists every reception including partial and failed ones (Incoming page history).</summary>
    public bool IncludeIncomplete { get; set; }

    public InboxViewModel(IFaxData data, ShellViewModel shell)
    {
        _data = data;
        _shell = shell;
        shell.InboundChanged += () => _ = RefreshAsync();
    }

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        List<ReceivedFax> rows;
        try { rows = await _data.ListReceivedAsync(IncludeIncomplete, Search); }
        catch (FaxServerException ex)
        {
            Serilog.Log.Warning("Could not refresh the Inbox: {Message}", ex.Message);
            return;
        }
        var selectedId = Selected?.Id;
        // Update in place: the shared Inbox refreshes whenever anyone reads a fax, and that must not disturb this list.
        for (int i = Items.Count - 1; i >= 0; i--)
            if (rows.All(r => r.Id != Items[i].Id)) Items.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            var existing = Items.FirstOrDefault(x => x.Id == rows[i].Id);
            if (existing is null) Items.Insert(Math.Min(i, Items.Count), new ReceivedItem(rows[i]));
            else
            {
                existing.Update(rows[i]);
                int at = Items.IndexOf(existing);
                if (at != i) Items.Move(at, Math.Min(i, Items.Count - 1));
            }
        }
        IsEmpty = Items.Count == 0;
        if (selectedId is not null) Selected = Items.FirstOrDefault(x => x.Id == selectedId);
        await _shell.RefreshUnreadAsync();
    }

    public async Task SetReadAsync(ReceivedItem item, bool read)
    {
        await _data.SetReadAsync(item.Id, read);
        item.Model.IsRead = read;
        item.Update(item.Model);
        await _shell.RefreshUnreadAsync();
    }

    public async Task RenameAsync(ReceivedItem item, string name)
    {
        await _data.RenameReceivedAsync(item.Id, name);
        item.Model.DisplayName = name;
        item.Update(item.Model);
    }

    public async Task DeleteAsync(ReceivedItem item)
    {
        await _data.DeleteReceivedAsync(item.Id);
        await RefreshAsync();
    }
}
