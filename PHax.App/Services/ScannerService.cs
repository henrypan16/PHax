using NAPS2.Images;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using PHax.Core.Models;
using PHax.Imaging.Conversion;
using PHax.Imaging.Encoding;
using Serilog;
using Serilog.Extensions.Logging;

namespace PHax.App.Services;

/// <summary>A scanner as listed in the Scan dialog.</summary>
public sealed class ScannerItem
{
    public required ScanDevice Device { get; init; }

    /// <summary>Stable key for <see cref="ScanSettings.Scanner"/>.</summary>
    public string Key => $"{Device.Driver}|{Device.ID}";

    public override string ToString() => $"{Device.Name} ({Device.Driver.ToString().ToUpperInvariant()})";
}

public sealed record ScanRequest(ScannerItem Scanner, PaperSource Source, string PageSize, BilevelMode Mode, bool UseScannerDialog);

/// <summary>
/// Scans paper documents into fax pages via NAPS2.Sdk (WIA and TWAIN). Runs in the app because driver dialogs need
/// the user's desktop session.
/// </summary>
public sealed class ScannerService : IDisposable
{
    // Fax fine resolution is 204 x 196 dpi; 200 dpi grayscale leaves thresholding/dithering to Bilevel.
    private const int ScanDpi = 200;

    private readonly Lazy<ScanningContext> _context = new(CreateContext);
    private List<ScannerItem>? _cached;

    /// <summary>
    /// TWAIN drivers are 32-bit and cannot load into this 64-bit process; NAPS2 hosts them in its x86 worker. The
    /// worker is only shipped in the x64 build, so ARM64 offers WIA only. (In-process 64-bit TWAIN is not used: it
    /// hung on a PC without a 64-bit TWAINDSM.dll.)
    /// </summary>
    public static bool HasWin32Worker => File.Exists(Path.Combine(AppContext.BaseDirectory, "NAPS2.Worker.exe"));

    private static ScanningContext CreateContext()
    {
        var context = new ScanningContext(new GdiImageContext())
        {
            Logger = new SerilogLoggerFactory(Log.Logger).CreateLogger("NAPS2"),
        };
        if (HasWin32Worker) context.SetUpWin32Worker();
        return context;
    }

    /// <summary>Lists WIA and TWAIN scanners. Each driver is queried separately so one broken driver does not hide the rest.</summary>
    public async Task<List<ScannerItem>> GetScannersAsync(bool refresh = false)
    {
        if (_cached is not null && !refresh) return _cached;
        var controller = new ScanController(_context.Value);
        var items = new List<ScannerItem>();

        async Task AddAsync(Driver driver)
        {
            try
            {
                foreach (var device in await controller.GetDeviceList(driver)) items.Add(new ScannerItem { Device = device });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Listing {Driver} scanners failed", driver);
            }
        }

        await AddAsync(Driver.Wia);
        if (HasWin32Worker) await AddAsync(Driver.Twain);
        Log.Information("Found {Count} scanner(s): {Scanners}", items.Count, string.Join("; ", items));
        return _cached = items;
    }

    /// <summary>Scans all pages (one for flatbed, until the feeder is empty otherwise) and converts them to fax pages.</summary>
    public async Task<List<FaxBitmap>> ScanAsync(ScanRequest request, IntPtr dialogParent, IProgress<string>? progress, CancellationToken ct)
    {
        var controller = new ScanController(_context.Value);
        controller.PageStart += (_, e) => progress?.Report($"Scanning page {e.PageNumber}…");
        var options = new ScanOptions
        {
            Device = request.Scanner.Device,
            Driver = request.Scanner.Device.Driver,
            PaperSource = request.Source,
            PageSize = PageSizeFor(request.PageSize),
            Dpi = ScanDpi,
            BitDepth = BitDepth.Grayscale,
            UseNativeUI = request.UseScannerDialog,
            DialogParent = dialogParent,
        };

        var paper = request.PageSize == "A4" ? PaperSize.A4 : PaperSize.Letter;
        var pages = new List<FaxBitmap>();
        await foreach (var image in controller.Scan(options, ct))
        {
            using (image)
            {
                pages.Add(await Task.Run(() =>
                {
                    using var bitmap = image.RenderToBitmap();
                    return Bilevel.FromImage(bitmap, paper, request.Mode);
                }, ct));
            }
        }
        Log.Information("Scanned {Pages} page(s) from {Scanner}", pages.Count, request.Scanner);
        return pages;
    }

    /// <summary>Writes scanned pages to a fax TIFF in the temp folder; the compose flow copies it into the job.</summary>
    public static string SaveScan(IReadOnlyList<FaxBitmap> pages)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PHax", "Scans");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.tif"))
        {
            try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var path = Path.Combine(dir, $"Scan {DateTime.Now:yyyy-MM-dd HHmmss}.tif");
        TiffFax.Write(path, pages);
        return path;
    }

    private static PageSize PageSizeFor(string name) => name switch
    {
        "A4" => PageSize.A4,
        "Legal" => PageSize.Legal,
        _ => PageSize.Letter,
    };

    public void Dispose()
    {
        if (_context.IsValueCreated) _context.Value.Dispose();
    }
}
