using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PHax.Core.Models;

namespace PHax.Imaging.Conversion;

public enum PaperSize { Letter, A4 }

public enum BilevelMode
{
    /// <summary>Threshold for documents, error diffusion for photographs (chosen per page from the histogram).</summary>
    Auto,
    Threshold,
    Dither,
}

/// <summary>Converts GDI+ bitmaps to fax pages (1728 pels wide, fine resolution).</summary>
public static class Bilevel
{
    public static int MaxFineLines(PaperSize paper) => paper == PaperSize.A4 ? 2287 : 2156;

    /// <summary>
    /// Scales <paramref name="source"/> to fit the fax page (keeping aspect ratio in physical units), centres it
    /// horizontally on a white page and converts to bilevel.
    /// </summary>
    public static FaxBitmap FromImage(Image source, PaperSize paper, BilevelMode mode, bool fillPageHeight = false)
    {
        int width = FaxBitmap.A4Width;
        int maxHeight = MaxFineLines(paper);

        // Physical aspect: fine fax pels are 1/204" wide and 1/196" tall.
        double srcAspect = (double)source.Height / source.Width;
        double targetH = width * srcAspect * (196.0 / 204.0);
        double scale = 1;
        if (targetH > maxHeight) scale = maxHeight / targetH;
        int drawW = Math.Max(1, (int)Math.Round(width * scale));
        int drawH = Math.Max(1, (int)Math.Round(targetH * scale));
        int pageH = fillPageHeight ? maxHeight : Math.Max(drawH, 1);

        using var canvas = new Bitmap(width, pageH, PixelFormat.Format32bppArgb);
        canvas.SetResolution(204, 196);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, new Rectangle((width - drawW) / 2, 0, drawW, drawH));
        }
        return FromCanvas(canvas, mode);
    }

    /// <summary>Converts a bitmap already at fax geometry (width 1728) to bilevel.</summary>
    public static FaxBitmap FromCanvas(Bitmap canvas, BilevelMode mode)
    {
        int w = canvas.Width, h = canvas.Height;
        var gray = new byte[w * h];
        var rect = new Rectangle(0, 0, w, h);
        var data = canvas.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w);
                for (int x = 0; x < w; x++)
                {
                    int argb = row[x];
                    int a = (argb >> 24) & 0xFF, r = (argb >> 16) & 0xFF, gch = (argb >> 8) & 0xFF, b = argb & 0xFF;
                    int lum = (r * 299 + gch * 587 + b * 114) / 1000;
                    // Composite transparent pixels on white.
                    gray[y * w + x] = (byte)((lum * a + 255 * (255 - a)) / 255);
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        if (mode == BilevelMode.Auto) mode = LooksPhotographic(gray) ? BilevelMode.Dither : BilevelMode.Threshold;
        var page = new FaxBitmap(w, h, FaxResolution.Fine);
        if (mode == BilevelMode.Threshold) Threshold(gray, w, h, page);
        else Dither(gray, w, h, page);
        return page;
    }

    private static bool LooksPhotographic(byte[] gray)
    {
        long mid = 0, nonWhite = 0;
        for (int i = 0; i < gray.Length; i += 7)
        {
            byte v = gray[i];
            if (v < 240) nonWhite++;
            if (v is > 60 and < 195) mid++;
        }
        return nonWhite > 0 && mid > nonWhite * 0.35;
    }

    private static void Threshold(byte[] gray, int w, int h, FaxBitmap page)
    {
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            if (gray[y * w + x] < 160) page.SetPixel(x, y, true);
    }

    /// <summary>Floyd–Steinberg error diffusion.</summary>
    private static void Dither(byte[] gray, int w, int h, FaxBitmap page)
    {
        var err = new int[w + 2];
        var next = new int[w + 2];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(next);
            for (int x = 0; x < w; x++)
            {
                int v = gray[y * w + x] + err[x + 1] / 16;
                int outV = v < 128 ? 0 : 255;
                if (outV == 0) page.SetPixel(x, y, true);
                int e = v - outV;
                err[x + 2] += e * 7;
                next[x] += e * 3;
                next[x + 1] += e * 5;
                next[x + 2] += e;
            }
            (err, next) = (next, err);
        }
    }

    /// <summary>Renders a fax page to a 1-bpp PNG for previews. Standard pages are line-doubled to keep the aspect ratio.</summary>
    public static byte[] ToPng(FaxBitmap page)
    {
        var fine = page.ToFine();
        using var bmp = ToGdiBitmap(fine);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public static Bitmap ToGdiBitmap(FaxBitmap page)
    {
        var bmp = new Bitmap(page.Width, Math.Max(1, page.Height), PixelFormat.Format1bppIndexed);
        bmp.SetResolution(204, (float)page.VerticalDpi);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < page.Height; y++)
            {
                var src = page.Row(y);
                for (int i = 0; i < src.Length; i++) row[i] = (byte)~src[i];
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
