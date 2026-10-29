using System.Globalization;

using PHax.Core.Models;

namespace PHax.Imaging.Encoding;

/// <summary>Writes fax pages to PDF, embedding each page as a CCITT Group 4 image (no re-rasterization).</summary>
public static class PdfFax
{
    public static void Write(string path, IReadOnlyList<FaxBitmap> pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) Write(fs, pages);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Write(Stream output, IReadOnlyList<FaxBitmap> pages)
    {
        var encoder = new T4Encoder();
        var offsets = new List<long>();
        long pos = 0;
        var inv = CultureInfo.InvariantCulture;

        void Raw(byte[] bytes) { output.Write(bytes); pos += bytes.Length; }
        void Text(string s) => Raw(System.Text.Encoding.Latin1.GetBytes(s));
        void BeginObject(int number)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = pos;
            Text($"{number} 0 obj\n");
        }

        Text("%PDF-1.4\n%âãÏÓ\n");
        int n = pages.Count;
        // Objects: 1 catalog, 2 pages, then for page i: 3+3i page, 4+3i contents, 5+3i image.
        BeginObject(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"));
        Text($"<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var page = pages[i];
            double w = page.Width / FaxBitmap.HorizontalDpi * 72.0;
            double h = page.Height / page.VerticalDpi * 72.0;
            string ws = w.ToString("0.###", inv), hs = h.ToString("0.###", inv);
            int pageObj = 3 + 3 * i, contentObj = pageObj + 1, imageObj = pageObj + 2;

            BeginObject(pageObj);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {ws} {hs}] /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            var content = System.Text.Encoding.Latin1.GetBytes($"q {ws} 0 0 {hs} 0 0 cm /Im0 Do Q\n");
            BeginObject(contentObj);
            Text($"<< /Length {content.Length} >>\nstream\n");
            Raw(content);
            Text("endstream\nendobj\n");

            var data = encoder.EncodeT6(page);
            BeginObject(imageObj);
            Text($"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
                 $"/Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {page.Width} /Rows {page.Height} /BlackIs1 false >> /Length {data.Length} >>\nstream\n");
            Raw(data);
            Text("\nendstream\nendobj\n");
        }

        long xref = pos;
        var sb = new System.Text.StringBuilder();
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", inv)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Text(sb.ToString());
    }
}
