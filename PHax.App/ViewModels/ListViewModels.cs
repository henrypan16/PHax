using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Ipc;
using PHax.Core.Models;
using PHax.Data;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.ViewModels;

/// <summary>Base for the Drafts / Outbox / Sent lists, which are filtered views of outbound jobs.</summary>
public abstract partial class OutboundListViewModel : ObservableObject
{
    protected readonly IDbContextFactory<FaxDbContext> Db;
    protected readonly ServiceClient Client;
    protected readonly FaxDocumentService Documents;

    public ObservableCollection<OutboundItem> Items { get; } = [];
    [ObservableProperty] public partial OutboundItem? Selected { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    protected OutboundListViewModel(IDbContextFactory<FaxDbContext> db, ServiceClient client, FaxDocumentService documents, ShellViewModel shell)
    {
        Db = db;
        Client = client;
        Documents = documents;
        shell.OutboundChanged += () => _ = RefreshAsync();
        shell.JobProgress += p => Items.FirstOrDefault(i => i.Id == p.JobId)?.Apply(p);
    }

    protected abstract IQueryable<OutboundFax> Filter(IQueryable<OutboundFax> q);

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var q = Filter(db.OutboundFaxes.AsNoTracking().Include(f => f.Attachments));
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(f => f.RecipientName.Contains(s) || f.FaxNumber.Contains(s) || f.Subject.Contains(s));
        }
        var rows = await q.OrderByDescending(f => f.UpdatedUtc).Take(500).ToListAsync();
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
        if (!Client.IsConnected) return "The PHax service is not running.";
        try { return await Client.RequestAsync<string>(command, new JobRef(Selected.Id)); }
        catch (Exception ex) { return ex.Message; }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is null) return;
        await Documents.DeleteOutboundAsync(Selected.Id);
        await RefreshAsync();
    }
}

public sealed class DraftsViewModel(IDbContextFactory<FaxDbContext> db, ServiceClient client, FaxDocumentService documents, ShellViewModel shell)
    : OutboundListViewModel(db, client, documents, shell)
{
    protected override IQueryable<OutboundFax> Filter(IQueryable<OutboundFax> q) => q.Where(f => f.Status == OutboundStatus.Draft);
}

public sealed class OutboxViewModel(IDbContextFactory<FaxDbContext> db, ServiceClient client, FaxDocumentService documents, ShellViewModel shell)
    : OutboundListViewModel(db, client, documents, shell)
{
    protected override IQueryable<OutboundFax> Filter(IQueryable<OutboundFax> q) =>
        q.Where(f => f.Status != OutboundStatus.Draft && f.Status != OutboundStatus.Sent);
}

public sealed class SentViewModel(IDbContextFactory<FaxDbContext> db, ServiceClient client, FaxDocumentService documents, ShellViewModel shell)
    : OutboundListViewModel(db, client, documents, shell)
{
    protected override IQueryable<OutboundFax> Filter(IQueryable<OutboundFax> q) => q.Where(f => f.Status == OutboundStatus.Sent);

    public Task ResendAsync() => Selected is null ? Task.CompletedTask : Documents.ResendAsync(Selected.Id);
}

public sealed partial class InboxViewModel : ObservableObject
{
    private readonly IDbContextFactory<FaxDbContext> _db;
    private readonly ShellViewModel _shell;
    private readonly FaxDocumentService _documents;

    public ObservableCollection<ReceivedItem> Items { get; } = [];
    [ObservableProperty] public partial ReceivedItem? Selected { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    /// <summary>When true, lists every reception including partial and failed ones (Incoming page history).</summary>
    public bool IncludeIncomplete { get; set; }

    public InboxViewModel(IDbContextFactory<FaxDbContext> db, ShellViewModel shell, FaxDocumentService documents)
    {
        _db = db;
        _shell = shell;
        _documents = documents;
        shell.InboundChanged += () => _ = RefreshAsync();
    }

    partial void OnSearchChanged(string value) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        var q = db.ReceivedFaxes.AsNoTracking().AsQueryable();
        q = IncludeIncomplete ? q.Where(r => r.Status != ReceivedStatus.Receiving) : q.Where(r => r.Status == ReceivedStatus.Complete);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(r => r.DisplayName.Contains(s) || (r.CallerId ?? "").Contains(s) || (r.RemoteId ?? "").Contains(s) || (r.CallerName ?? "").Contains(s));
        }
        var rows = await q.OrderByDescending(r => r.ReceivedUtc).Take(IncludeIncomplete ? 30 : 1000).ToListAsync();
        var selectedId = Selected?.Id;
        Items.Clear();
        foreach (var r in rows) Items.Add(new ReceivedItem(r));
        IsEmpty = Items.Count == 0;
        if (selectedId is not null) Selected = Items.FirstOrDefault(x => x.Id == selectedId);
        await _shell.RefreshUnreadAsync();
    }

    public async Task SetReadAsync(ReceivedItem item, bool read)
    {
        await using var db = await _db.CreateDbContextAsync();
        await db.ReceivedFaxes.Where(r => r.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRead, read));
        item.Model.IsRead = read;
        item.Update(item.Model);
        await _shell.RefreshUnreadAsync();
    }

    public async Task RenameAsync(ReceivedItem item, string name)
    {
        await using var db = await _db.CreateDbContextAsync();
        await db.ReceivedFaxes.Where(r => r.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DisplayName, name));
        item.Model.DisplayName = name;
        item.Update(item.Model);
    }

    public async Task DeleteAsync(ReceivedItem item)
    {
        await _documents.DeleteReceivedAsync(item.Id);
        await RefreshAsync();
    }
}
