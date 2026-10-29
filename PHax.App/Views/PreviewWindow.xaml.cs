using PHax.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PHax.App.Views;

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
        PageInfo.Text = $"{pages.Count} page(s)";
    }

    public static void Open(string name, IReadOnlyList<FaxBitmap> pages)
    {
        var window = new PreviewWindow(name, pages);
        window.Activate();
        _ = window.RenderAsync();
    }

    private async Task RenderAsync()
    {
        foreach (var page in _pages)
        {
            var image = new Image { Source = await FaxActions.ToImageAsync(page), Width = 816, Stretch = Stretch.Uniform };
            PagesPanel.Children.Add(new Border
            {
                Child = image,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            });
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e) => await FaxActions.PrintAsync(Content.XamlRoot, _pages, _name);

    private async void SavePdf_Click(object sender, RoutedEventArgs e) => await FaxActions.SavePdfAsync(this, Content.XamlRoot, _pages, _name);
}
