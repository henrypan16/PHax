using System.Drawing;
using System.Drawing.Imaging;
using PHax.Core.Models;
using PHax.Imaging.Encoding;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PHax.Imaging.Conversion;

/// <summary>
/// Converts attachments (PDF, TIFF, PNG, JPEG, BMP, GIF, and Word documents via <see cref="OfficeConverter"/>)
/// into fine-resolution fax pages.
/// </summary>
public sealed class DocumentRasterizer
{
    public static readonly string[] SupportedExtensions =
        [".pdf", ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".bmp", ".gif", .. OfficeConverter.Extensions];

    public PaperSize Paper { get; init; } = PaperSize.Letter;
    public BilevelMode Mode { get; init; } = BilevelMode.Auto;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".tif" or ".tiff" => "image/tiff",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".rtf" => "application/rtf",
        ".odt" => "application/vnd.oasis.opendocument.text",
        _ => "application/octet-stream",
    };

    public async Task<List<FaxBitmap>> RasterizeAsync(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (OfficeConverter.IsOfficeDocument(path))
        {
            var pdf = await Task.Run(() => OfficeConverter.ToPdf(path), ct);
            try { return await RasterizePdfAsync(pdf, ct); }
            finally { try { File.Delete(pdf); } catch (IOException) { } }
        }
        return ext switch
        {
            ".pdf" => await RasterizePdfAsync(path, ct),
            ".tif" or ".tiff" => RasterizeTiff(path),
            _ when IsSupported(path) => RasterizeImage(path),
            _ => throw new NotSupportedException($"Unsupported file type '{ext}'."),
        };
    }

    public async Task<int> CountPagesAsync(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (OfficeConverter.IsOfficeDocument(path)) return (await RasterizeAsync(path)).Count;
        if (ext == ".pdf")
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var doc = await PdfDocument.LoadFromFileAsync(file);
            return (int)doc.PageCount;
        }
        if (ext is ".tif" or ".tiff")
        {
            using var img = Image.FromFile(path);
            return img.GetFrameCount(FrameDimension.Page);
        }
        return 1;
    }

    private List<FaxBitmap> RasterizeImage(string path)
    {
        using var img = Image.FromFile(path);
        return [Bilevel.FromImage(img, Paper, Mode)];
    }

    private List<FaxBitmap> RasterizeTiff(string path)
    {
        // Bilevel fax TIFFs at fax geometry are decoded natively to avoid resampling.
        try
        {
            var native = TiffFax.Read(path);
            if (native.Count > 0 && native.All(p => p.Width == FaxBitmap.A4Width))
                return native.Select(p => p.ToFine()).ToList();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or Decoding.FaxDecodeException or IndexOutOfRangeException or ArgumentException)
        {
            // Fall through to GDI+.
        }

        var pages = new List<FaxBitmap>();
        using var img = Image.FromFile(path);
        int frames = img.GetFrameCount(FrameDimension.Page);
        for (int i = 0; i < frames; i++)
        {
            img.SelectActiveFrame(FrameDimension.Page, i);
            using var frame = new Bitmap(img);
            pages.Add(Bilevel.FromImage(frame, Paper, Mode));
        }
        return pages;
    }

    private async Task<List<FaxBitmap>> RasterizePdfAsync(string path, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var doc = await PdfDocument.LoadFromFileAsync(file);
        var pages = new List<FaxBitmap>();
        int maxLines = Bilevel.MaxFineLines(Paper);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            // Page size is in DIPs (1/96"). Render directly at 204 x 196 dpi, scaled down to fit the fax page.
            double wPx = page.Size.Width / 96.0 * 204.0;
            double hPx = page.Size.Height / 96.0 * 196.0;
            double scale = Math.Min(1.0, Math.Min(FaxBitmap.A4Width / wPx, maxLines / hPx));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(wPx * scale)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(hPx * scale)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            using var managed = stream.AsStreamForRead();
            using var rendered = new Bitmap(managed);
            using var canvas = new Bitmap(FaxBitmap.A4Width, rendered.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(rendered, (FaxBitmap.A4Width - rendered.Width) / 2, 0);
            }
            pages.Add(Bilevel.FromCanvas(canvas, Mode == BilevelMode.Auto ? BilevelMode.Threshold : Mode));
        }
        return pages;
    }
}
