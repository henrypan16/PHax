using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using PHax.Core.Models;

namespace PHax.Imaging.Conversion;

public sealed record CoverPageInfo(
    string ToName,
    string ToFax,
    string From,
    string FromFax,
    string Subject,
    string Message,
    int TotalPages,
    DateTime LocalTime)
{
    public string ToAddress { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string FromPhone { get; init; } = "";
}

public static class CoverPageRenderer
{
    public static FaxBitmap Render(CoverPageInfo info, PaperSize paper = PaperSize.Letter)
    {
        int width = FaxBitmap.A4Width;
        int height = Bilevel.MaxFineLines(paper);
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        // 204 x 196 dpi is close to square; GDI+ point sizes use the bitmap DPI.
        canvas.SetResolution(204, 196);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            using var title = new Font("Segoe UI", 40, FontStyle.Bold, GraphicsUnit.Point);
            using var label = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Point);
            using var value = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Point);
            using var body = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Point);
            using var pen = new Pen(Color.Black, 4);

            int margin = 150;
            float y = 160;
            g.DrawString("FAX", title, Brushes.Black, margin, y);
            y += 190;
            g.DrawLine(pen, margin, y, width - margin, y);
            y += 40;

            void Field(string name, string text)
            {
                g.DrawString(name, label, Brushes.Black, margin, y);
                g.DrawString(text, value, Brushes.Black, margin + 360, y);
                y += 70;
            }

            Field("To:", info.ToName);
            if (!string.IsNullOrWhiteSpace(info.ToAddress)) Field("Address:", info.ToAddress);
            Field("Fax:", info.ToFax);
            Field("From:", info.From);
            if (!string.IsNullOrWhiteSpace(info.FromAddress)) Field("Address:", info.FromAddress);
            if (!string.IsNullOrWhiteSpace(info.FromPhone)) Field("Phone:", info.FromPhone);
            if (!string.IsNullOrWhiteSpace(info.FromFax)) Field("Reply fax:", info.FromFax);
            Field("Date:", info.LocalTime.ToString("f"));
            Field("Pages:", $"{info.TotalPages} (including cover)");
            if (!string.IsNullOrWhiteSpace(info.Subject)) Field("Subject:", info.Subject);

            y += 20;
            g.DrawLine(pen, margin, y, width - margin, y);
            y += 50;
            if (!string.IsNullOrWhiteSpace(info.Message))
            {
                var area = new RectangleF(margin, y, width - 2 * margin, height - y - 150);
                g.DrawString(info.Message, body, Brushes.Black, area);
            }
        }
        return Bilevel.FromCanvas(canvas, BilevelMode.Threshold);
    }
}
