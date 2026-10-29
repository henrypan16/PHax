using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PHax.Imaging.Conversion;

/// <summary>
/// Converts word-processing documents to PDF using Microsoft Word (COM automation) or, if Word is not installed,
/// LibreOffice in headless mode. Must run in the user's session (the desktop app), not in the service.
/// </summary>
public static class OfficeConverter
{
    public static readonly string[] Extensions = [".doc", ".docx", ".rtf", ".odt"];

    public static bool IsOfficeDocument(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Returns the path of a temporary PDF; the caller deletes it.</summary>
    public static string ToPdf(string path)
    {
        var output = Path.Combine(Path.GetTempPath(), $"phax-{Guid.NewGuid():N}.pdf");
        var errors = new List<string>();
        if (Type.GetTypeFromProgID("Word.Application") is { } wordType)
        {
            try
            {
                RunSta(() => ConvertWithWord(wordType, Path.GetFullPath(path), output));
                if (File.Exists(output)) return output;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                errors.Add($"Word: {ex.Message}");
            }
        }
        if (FindLibreOffice() is { } soffice)
        {
            var converted = ConvertWithLibreOffice(soffice, path);
            if (converted is not null)
            {
                File.Move(converted, output, overwrite: true);
                return output;
            }
            errors.Add("LibreOffice could not convert the document.");
        }
        throw new NotSupportedException(
            $"Cannot convert {Path.GetFileName(path)} to fax pages: Microsoft Word or LibreOffice is required for {Path.GetExtension(path)} files." +
            (errors.Count > 0 ? " " + string.Join(" ", errors) : ""));
    }

    private static void ConvertWithWord(Type wordType, string input, string output)
    {
        dynamic word = Activator.CreateInstance(wordType)!;
        try
        {
            word.Visible = false;
            word.DisplayAlerts = 0;
            // Documents.Open(FileName, ConfirmConversions: false, ReadOnly: true)
            dynamic doc = word.Documents.Open(input, false, true);
            try
            {
                doc.ExportAsFixedFormat(output, 17); // wdExportFormatPDF
            }
            finally
            {
                doc.Close(0);
                Marshal.FinalReleaseComObject(doc);
            }
        }
        finally
        {
            word.Quit(0);
            Marshal.FinalReleaseComObject(word);
        }
    }

    private static string? FindLibreOffice()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Path.Combine(Environment.GetFolderPath(root), "LibreOffice", "program", "soffice.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? ConvertWithLibreOffice(string soffice, string input)
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"phax-lo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo(soffice)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", outDir, Path.GetFullPath(input) },
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return null;
        }
        return Directory.GetFiles(outDir, "*.pdf").FirstOrDefault();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(3))) throw new InvalidOperationException("Word did not finish converting the document within 3 minutes.");
        if (error is not null) throw error;
    }
}
