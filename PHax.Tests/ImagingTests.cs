using System.Drawing;
using System.Drawing.Imaging;
using PHax.Core.Models;
using PHax.Imaging.Conversion;
using PHax.Imaging.Decoding;
using PHax.Imaging.Encoding;
using Xunit;

namespace PHax.Tests;

[Trait("Category", "Unit")]
public class ImagingTests
{
    public static TheoryData<string> PageKinds => ["text", "noise", "sparse", "patterns"];

    private static FaxBitmap Page(string kind) => kind switch
    {
        "text" => TestPages.Text(),
        "noise" => TestPages.Noise(),
        "sparse" => TestPages.Noise(400, 7, 0.02),
        _ => TestPages.Patterns(),
    };

    [Fact]
    public void RunLengthCodeTables_ArePrefixFree()
    {
        foreach (var black in new[] { false, true })
        {
            var codes = T4Codes.RunCodes(black).Select(c => c.Code).Append("000000000001").ToList();
            Assert.Equal(codes.Count, codes.Distinct().Count());
            foreach (var a in codes)
            foreach (var b in codes)
                if (a != b) Assert.False(b.StartsWith(a, StringComparison.Ordinal), $"{a} is a prefix of {b} ({(black ? "black" : "white")})");
        }
        Assert.Equal(64, T4Codes.WhiteTerminating.Length);
        Assert.Equal(64, T4Codes.BlackTerminating.Length);
        Assert.Equal(27, T4Codes.WhiteMakeup.Length);
        Assert.Equal(27, T4Codes.BlackMakeup.Length);
        Assert.Equal(13, T4Codes.ExtendedMakeup.Length);
    }

    [Theory]
    [MemberData(nameof(PageKinds))]
    public void T4_RoundTrip_AllCompressions(string kind)
    {
        var page = Page(kind);
        var encoder = new T4Encoder();
        var decoder = new T4Decoder();
        foreach (var compression in new[] { FaxCompression.MH, FaxCompression.MR })
        foreach (var lsb in new[] { false, true })
        foreach (var minBits in new[] { 0, 14400 * 20 / 1000 })
        {
            var data = encoder.EncodeT4(page, compression, minBits, lsb);
            var result = decoder.DecodeT4(data, page.Width, page.Resolution, compression, lsb);
            Assert.True(result.RtcFound, $"RTC not found ({compression}, lsb={lsb})");
            Assert.Equal(0, result.BadLines);
            Assert.True(page.ContentEquals(result.Page), $"Mismatch {kind} {compression} lsb={lsb} minBits={minBits}");
        }
    }

    [Fact]
    public void T4_MinimumScanLineTime_IsHonoured()
    {
        var page = new FaxBitmap(FaxBitmap.A4Width, 100, FaxResolution.Standard); // white lines code very short
        int minBits = 9600 * 20 / 1000;
        var data = new T4Encoder().EncodeT4(page, FaxCompression.MH, minBits, lsbFirst: false);
        Assert.True(data.Length * 8 >= page.Height * minBits);
    }

    [Theory]
    [MemberData(nameof(PageKinds))]
    public void T6_RoundTrip(string kind)
    {
        var page = Page(kind);
        var data = new T4Encoder().EncodeT6(page);
        var back = new T4Decoder().DecodeT6(data, page.Width, page.Height, page.Resolution);
        Assert.True(page.ContentEquals(back));
    }

    [Fact]
    public void T4_CorruptedLine_IsCountedAndConcealed()
    {
        var page = TestPages.Text();
        var data = new T4Encoder().EncodeT4(page, FaxCompression.MH, 0, lsbFirst: false);
        for (int i = data.Length / 2; i < data.Length / 2 + 40; i++) data[i] ^= 0xA5;
        var result = new T4Decoder().DecodeT4(data, page.Width, page.Resolution, FaxCompression.MH, lsbFirst: false);
        Assert.True(result.BadLines > 0);
        Assert.True(result.GoodLines > page.Height / 2);
        Assert.True(result.RtcFound);
    }

    /// <summary>Independent oracle: GDI+'s CCITT decoder must read our Group 4 TIFF identically.</summary>
    [Theory]
    [MemberData(nameof(PageKinds))]
    public void Tiff_Group4_DecodesIdenticallyInGdiPlus(string kind)
    {
        var page = Page(kind);
        var path = Path.Combine(Path.GetTempPath(), $"phax-test-{Guid.NewGuid():N}.tif");
        try
        {
            TiffFax.Write(path, [page, TestPages.Patterns()]);
            using (var img = (Bitmap)Image.FromFile(path))
            {
                Assert.Equal(2, img.GetFrameCount(FrameDimension.Page));
                img.SelectActiveFrame(FrameDimension.Page, 0);
                Assert.Equal(page.Width, img.Width);
                Assert.Equal(page.Height, img.Height);
                for (int y = 0; y < page.Height; y += 3)
                for (int x = 0; x < page.Width; x += 1)
                {
                    bool gdiBlack = img.GetPixel(x, y).GetBrightness() < 0.5f;
                    if (gdiBlack != page.GetPixel(x, y)) Assert.Fail($"Pixel mismatch at {x},{y}");
                }
            }
            var back = TiffFax.Read(path);
            Assert.Equal(2, back.Count);
            Assert.True(page.ContentEquals(back[0]));
            Assert.Equal(FaxResolution.Fine, back[0].Resolution);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdf_IsWellFormed()
    {
        using var ms = new MemoryStream();
        PdfFax.Write(ms, [TestPages.Text(), TestPages.Patterns(FaxResolution.Standard)]);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/CCITTFaxDecode", text);
        Assert.Contains("/Count 2", text);
        // Every xref offset must point at "n 0 obj".
        int xref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        var lines = text[xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        for (int i = 1; i < count; i++)
        {
            int offset = int.Parse(lines[2 + i][..10]);
            Assert.StartsWith($"{i} 0 obj", text[offset..]);
        }
    }

    [Fact]
    public void Resolution_Conversions()
    {
        var fine = TestPages.Noise(301, 3, 0.1);
        var std = fine.ToStandard();
        Assert.Equal(151, std.Height);
        Assert.Equal(FaxResolution.Standard, std.Resolution);
        Assert.Equal(302, std.ToFine().Height);
    }

    [Fact]
    public async Task Rasterizer_ConvertsPdfTiffPngAndJpeg()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"phax-raster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var text = TestPages.Text();
            PdfFax.Write(Path.Combine(dir, "doc.pdf"), [text, TestPages.Patterns()]);
            TiffFax.Write(Path.Combine(dir, "doc.tif"), [text]);
            using (var bmp = new Bitmap(800, 1000))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.FillRectangle(Brushes.Black, 100, 100, 600, 80);
                }
                bmp.Save(Path.Combine(dir, "img.png"), ImageFormat.Png);
                bmp.Save(Path.Combine(dir, "img.jpg"), ImageFormat.Jpeg);
                bmp.Save(Path.Combine(dir, "img.bmp"), ImageFormat.Bmp);
            }

            var rasterizer = new DocumentRasterizer();
            var pdf = await rasterizer.RasterizeAsync(Path.Combine(dir, "doc.pdf"));
            Assert.Equal(2, pdf.Count);
            Assert.Equal(2, await rasterizer.CountPagesAsync(Path.Combine(dir, "doc.pdf")));
            Assert.All(pdf, p => Assert.Equal(FaxBitmap.A4Width, p.Width));
            // Rendering the fax PDF back must reproduce the page closely (anti-aliasing allows small differences).
            int diff = 0, black = 0;
            var back = pdf[0];
            for (int y = 0; y < Math.Min(text.Height, back.Height); y += 2)
            for (int x = 0; x < text.Width; x += 2)
            {
                if (text.GetPixel(x, y)) black++;
                if (text.GetPixel(x, y) != back.GetPixel(x, y)) diff++;
            }
            Assert.True(black > 0 && diff < black / 4, $"PDF render differs too much: {diff} of {black}");

            var tif = await rasterizer.RasterizeAsync(Path.Combine(dir, "doc.tif"));
            Assert.True(text.ContentEquals(Assert.Single(tif)));
            foreach (var name in new[] { "img.png", "img.jpg", "img.bmp" })
            {
                var page = Assert.Single(await rasterizer.RasterizeAsync(Path.Combine(dir, name)));
                Assert.Equal(FaxBitmap.A4Width, page.Width);
                Assert.True(page.GetPixel(864, 300), $"{name}: bar missing");
                Assert.False(page.GetPixel(864, 1500), $"{name}: unexpected black");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bilevel_ConvertsImagesToFaxGeometry()
    {
        using var bmp = new Bitmap(850, 1100);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.FillRectangle(Brushes.Black, 100, 100, 200, 50);
        }
        var page = Bilevel.FromImage(bmp, PaperSize.Letter, BilevelMode.Threshold);
        Assert.Equal(FaxBitmap.A4Width, page.Width);
        Assert.InRange(page.Height, 2000, Bilevel.MaxFineLines(PaperSize.Letter));
        Assert.True(page.GetPixel(400, 250));
        Assert.False(page.GetPixel(1500, 1500));
    }
}
