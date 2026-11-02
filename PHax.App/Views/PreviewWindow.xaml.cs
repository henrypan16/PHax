using PHax.Core.Models;
using Microsoft.UI.Xaml;

namespace PHax.App.Views;

/// <summary>Full-size preview of a fax (received, queued, sent, or being composed) in its own window.</summary>
public sealed partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<FaxBitmap> _pages;
    private readonly string _name;

    private PreviewWindow(string name, IReadOnlyList<FaxBitmap> pages)
    {
        _name = name;
        _pages = pages;
        InitializeComponent();
        Title = $"{name} — preview";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 1000));
        PHax.App.Services.AppIcon.Apply(AppWindow);
        Pane.AddShortcutsTo((UIElement)Content);
    }

    public static void Open(string name, IReadOnlyList<FaxBitmap> pages)
    {
        var window = new PreviewWindow(name, pages);
        window.Activate();
        window.Pane.ShowPages(name, pages);
    }

    private async void Print_Click(object sender, RoutedEventArgs e) => await FaxActions.PrintAsync(Content.XamlRoot, _pages, _name);

    private async void SavePdf_Click(object sender, RoutedEventArgs e) => await FaxActions.SavePdfAsync(this, Content.XamlRoot, _pages, _name);
}
