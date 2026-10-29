using System.Drawing;
using System.Drawing.Printing;
using PHax.Core.Models;

namespace PHax.Imaging.Conversion;

/// <summary>Prints fax pages through GDI+ to a named (or the default) printer, scaled to the printable area.</summary>
public static class FaxPrinter
{
    public static IReadOnlyList<string> InstalledPrinters() =>
        PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    public static string DefaultPrinter() => new PrinterSettings().PrinterName;

    public static void Print(IReadOnlyList<FaxBitmap> pages, string? printerName, string documentName)
    {
        if (pages.Count == 0) return;
        // StandardPrintController never shows a progress dialog (required when printing from the service).
        using var doc = new PrintDocument { DocumentName = documentName, PrintController = new StandardPrintController() };
        if (!string.IsNullOrEmpty(printerName)) doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException(string.IsNullOrEmpty(printerName)
                ? "No default printer is set."
                : $"Printer \"{printerName}\" is not available. Printers installed only for one Windows user are not visible to the PHax service.");
        int index = 0;
        doc.PrintPage += (_, e) =>
        {
            using var bmp = Bilevel.ToGdiBitmap(pages[index].ToFine());
            var area = e.MarginBounds;
            double scale = Math.Min(area.Width / (bmp.Width / 204.0 * 100), area.Height / (bmp.Height / 196.0 * 100));
            scale = Math.Min(scale, 1.0);
            int w = (int)(bmp.Width / 204.0 * 100 * scale), h = (int)(bmp.Height / 196.0 * 100 * scale);
            e.Graphics!.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImage(bmp, new Rectangle(area.Left + (area.Width - w) / 2, area.Top, w, h));
            index++;
            e.HasMorePages = index < pages.Count;
        };
        doc.Print();
    }
}
