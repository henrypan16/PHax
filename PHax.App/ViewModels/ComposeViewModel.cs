using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PHax.App.Services;
using PHax.Core.Models;
using PHax.Core.Services;
using PHax.Data;
using PHax.Imaging.Conversion;
using Microsoft.EntityFrameworkCore;

namespace PHax.App.ViewModels;

public sealed partial class AttachmentItem : ObservableObject
{
    public required ComposeAttachment Source { get; init; }
    public string Name => Source.FileName;
    [ObservableProperty] public partial string PagesText { get; set; } = "…";
}

public sealed partial class ComposeViewModel(FaxDocumentService documents, IDbContextFactory<FaxDbContext> db, SettingsStore settings) : ObservableObject
{
    private Guid? _draftId;

    [ObservableProperty] public partial string RecipientName { get; set; } = "";
    [ObservableProperty] public partial string FaxNumber { get; set; } = "";
    [ObservableProperty] public partial string Subject { get; set; } = "";
    [ObservableProperty] public partial bool IncludeCoverPage { get; set; }
    [ObservableProperty] public partial string CoverMessage { get; set; } = "";
    [ObservableProperty] public partial AttachmentItem? SelectedAttachment { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public ObservableCollection<Recipient> RecentRecipients { get; } = [];

    public async Task InitializeAsync(Guid? draftId, IEnumerable<string>? initialFiles)
    {
        var s = await settings.LoadAsync();
        IncludeCoverPage = s.Outgoing.DefaultCoverPage;
        await using var ctx = await db.CreateDbContextAsync();
        foreach (var r in await ctx.Recipients.AsNoTracking().OrderByDescending(r => r.LastUsedUtc).Take(50).ToListAsync()) RecentRecipients.Add(r);
        if (draftId is { } id)
        {
            var draft = await ctx.OutboundFaxes.AsNoTracking().Include(f => f.Attachments).FirstAsync(f => f.Id == id);
            _draftId = id;
            RecipientName = draft.RecipientName;
            FaxNumber = draft.FaxNumber;
            Subject = draft.Subject;
            IncludeCoverPage = draft.IncludeCoverPage;
            CoverMessage = draft.CoverMessage;
            var paths = App.GetService<AppPaths>();
            foreach (var a in draft.Attachments.OrderBy(a => a.Order))
                Attachments.Add(new AttachmentItem
                {
                    Source = new ComposeAttachment { ExistingId = a.Id, FileName = a.OriginalFileName, Path = paths.Resolve(a.StoredPath), PageCount = a.PageCount },
                    PagesText = a.PageCount > 0 ? $"{a.PageCount} page(s)" : "",
                });
        }
        foreach (var f in initialFiles ?? []) await AddFileAsync(f);
    }

    public async Task AddFileAsync(string path)
    {
        if (!DocumentRasterizer.IsSupported(path))
        {
            Status = $"{Path.GetFileName(path)}: unsupported file type. Use PDF, TIFF, PNG, JPEG or BMP.";
            return;
        }
        var item = new AttachmentItem { Source = new ComposeAttachment { FileName = Path.GetFileName(path), Path = path } };
        Attachments.Add(item);
        SelectedAttachment = item;
        try
        {
            int pages = await Task.Run(() => new DocumentRasterizer().CountPagesAsync(path));
            item.Source.PageCount = pages;
            item.PagesText = $"{pages} page(s)";
        }
        catch (Exception ex)
        {
            item.PagesText = "unreadable";
            Status = $"{item.Name}: {ex.Message}";
        }
    }

    public void Move(int delta)
    {
        if (SelectedAttachment is null) return;
        int i = Attachments.IndexOf(SelectedAttachment);
        int j = Math.Clamp(i + delta, 0, Attachments.Count - 1);
        if (i != j) Attachments.Move(i, j);
    }

    public void RemoveSelected()
    {
        if (SelectedAttachment is not null) Attachments.Remove(SelectedAttachment);
    }

    public string TotalPagesText =>
        $"{Attachments.Sum(a => a.Source.PageCount) + (IncludeCoverPage ? 1 : 0)} page(s) total";

    public async Task<bool> SaveAsync(bool send)
    {
        if (send && !DialString.IsValid(FaxNumber)) { Status = "Enter a valid fax number."; return false; }
        IsBusy = true;
        try
        {
            var data = new ComposeData
            {
                DraftId = _draftId,
                RecipientName = RecipientName,
                FaxNumber = FaxNumber,
                Subject = Subject,
                IncludeCoverPage = IncludeCoverPage,
                CoverMessage = CoverMessage,
                Attachments = Attachments.Select(a => a.Source).ToList(),
            };
            var job = await documents.SaveAsync(data, send, new Progress<string>(m => Status = m));
            _draftId = job.Status == OutboundStatus.Draft ? job.Id : null;
            Status = send ? $"Queued: {job.PageCount} page(s) to {job.FaxNumber}." : "Draft saved.";
            App.GetService<ShellViewModel>().RaiseOutboundChanged();
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
