using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PHax.App.Views;

/// <summary>A text box with a Browse button that picks a folder.</summary>
public sealed partial class FolderField : UserControl
{
    private readonly TextBox _box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(nameof(Path), typeof(string), typeof(FolderField),
        new PropertyMetadata("", (d, e) => ((FolderField)d)._box.Text = e.NewValue as string ?? ""));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(FolderField),
        new PropertyMetadata("", (d, e) => ((FolderField)d)._box.Header = e.NewValue));

    public string Path { get => (string)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public FolderField()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _box.TextChanged += (_, _) => Path = _box.Text;
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            var folder = await Pickers.FolderAsync(App.Shell);
            if (folder is not null) Path = folder;
        };
        Grid.SetColumn(browse, 1);
        grid.Children.Add(_box);
        grid.Children.Add(browse);
        Content = grid;
    }
}
