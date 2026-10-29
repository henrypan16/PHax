using PHax.Core.Models;
using PHax.Imaging.Conversion;
using PHax.Imaging.Encoding;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PHax.App.Views;

/// <summary>Functions for x:Bind.</summary>
public static class Bind
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfPositive(int value) => value > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
    public static bool IsNotNull(object? value) => value is not null;
    public static Brush StatusBrush(bool connected, bool problem) =>
        new SolidColorBrush(!connected ? Colors.Gray : problem ? Colors.Orange : Colors.LimeGreen);
}

public static class Dialogs
{
    private static XamlRoot? Root(XamlRoot? root) => root ?? App.Shell?.Content.XamlRoot;

    public static async Task MessageAsync(XamlRoot? root, string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = "OK", XamlRoot = Root(root) };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot? root, string title, string message, string confirm = "OK")
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = Root(root),
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PromptAsync(XamlRoot? root, string title, string initial)
    {
        var box = new TextBox { Text = initial };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root(root) };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task<string?> ChoosePrinterAsync(XamlRoot? root)
    {
        var printers = FaxPrinter.InstalledPrinters();
        if (printers.Count == 0) { await MessageAsync(root, "Print", "No printers are installed."); return null; }
        var combo = new ComboBox { ItemsSource = printers, SelectedItem = FaxPrinter.DefaultPrinter(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { Title = "Print fax", Content = combo, PrimaryButtonText = "Print", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root(root) };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? combo.SelectedItem as string : null;
    }
}

public static class Pickers
{
    private static IntPtr Hwnd(Window? window) => WindowNative.GetWindowHandle(window ?? App.Shell!);

    public static async Task<IReadOnlyList<string>> OpenFilesAsync(Window? window, IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        InitializeWithWindow.Initialize(picker, Hwnd(window));
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<string?> OpenFileAsync(Window? window, params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        InitializeWithWindow.Initialize(picker, Hwnd(window));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveFileAsync(Window? window, string suggestedName, string label, params string[] extensions)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(label, extensions.ToList());
        InitializeWithWindow.Initialize(picker, Hwnd(window));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static async Task<string?> FolderAsync(Window? window)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd(window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}

public static class FaxActions
{
    public static async Task<ImageSource> ToImageAsync(FaxBitmap page)
    {
        var png = await Task.Run(() => Bilevel.ToPng(page));
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    public static async Task PrintAsync(XamlRoot? root, IReadOnlyList<FaxBitmap> pages, string name)
    {
        var printer = await Dialogs.ChoosePrinterAsync(root);
        if (printer is null) return;
        try { await Task.Run(() => FaxPrinter.Print(pages, printer, name)); }
        catch (Exception ex) { await Dialogs.MessageAsync(root, "Print failed", ex.Message); }
    }

    public static async Task SavePdfAsync(Window? window, XamlRoot? root, IReadOnlyList<FaxBitmap> pages, string name)
    {
        var path = await Pickers.SaveFileAsync(window, Sanitize(name), "PDF document", ".pdf");
        if (path is null) return;
        await Task.Run(() => PdfFax.Write(path, pages));
    }

    public static async Task SaveTiffAsync(Window? window, IReadOnlyList<FaxBitmap> pages, string name)
    {
        var path = await Pickers.SaveFileAsync(window, Sanitize(name), "TIFF image", ".tif");
        if (path is null) return;
        await Task.Run(() => TiffFax.Write(path, pages));
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "fax" : s;
    }
}

internal static class BufferExtensions
{
    public static IBuffer AsBuffer(this byte[] data) => System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(data);
}
