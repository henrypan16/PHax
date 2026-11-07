using PHax.App.Services;
using PHax.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace PHax.App.Views;

/// <summary>
/// Shows every page of one fax with zoom and page navigation. Used as the reading pane next to the fax lists and as
/// the body of <see cref="PreviewWindow"/>.
/// </summary>
public sealed partial class FaxPreviewPane : UserControl
{
    // Fax pages are 1728 pels at 204 dpi (8.47"); 816 DIPs is 8.5" at 96 DIPs/inch, i.e. roughly actual size at 100%.
    private const double PageWidth = 816;
    private const float ZoomStep = 1.25f;
    // Coalesces list refreshes (which briefly clear and restore the selection) and fast keyboard navigation.
    private const int DebounceMs = 120;

    private CancellationTokenSource? _load;
    private string? _requestedKey;
    private string? _shownKey;
    private IReadOnlyList<FaxBitmap> _pages = [];
    private string _title = "";
    private int _currentPage;
    // Fit the page width (also when the pane is resized) until the user zooms.
    private bool _autoFit = true;

    public FaxPreviewPane()
    {
        InitializeComponent();
        HintText.Text = EmptyText;
    }

    /// <summary>Shown when nothing is selected.</summary>
    public string EmptyText { get; set; } = "Select a fax to preview it here.";

    /// <summary>
    /// Decode width for page images; 0 = full resolution (1728 px). The reading pane uses less to keep memory down
    /// for long faxes, at the cost of some sharpness when zoomed in.
    /// </summary>
    public int DecodePixelWidth { get; set; } = 1224;

    public bool ShowOpenButton
    {
        get => OpenButton.Visibility == Visibility.Visible;
        set => OpenButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the document identified by <paramref name="key"/> (e.g. its stored path). Nothing is reloaded when that
    /// document is already shown; pass a key that changes with the content to force a reload.
    /// </summary>
    public async void Show(string? key, string title, Func<CancellationToken, Task<IReadOnlyList<FaxBitmap>>>? load, string? emptyText = null)
    {
        if (key == _requestedKey) return;
        _requestedKey = key;
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        try
        {
            await Task.Delay(DebounceMs, cts.Token);
            if (key == _shownKey) return;
            Reset();
            _shownKey = key;
            if (key is null || load is null)
            {
                ShowHint(emptyText ?? EmptyText);
                return;
            }
            TitleText.Text = _title = title;
            Ring.IsActive = true;
            var pages = await load(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (pages.Count == 0) { ShowHint("This fax has no pages."); return; }
            _pages = pages;
            Tools.IsEnabled = true;
            UpdatePageInfo();
            foreach (var page in pages)
            {
                var image = new Image { Source = await FaxActions.ToImageAsync(page, DecodePixelWidth), Width = PageWidth, Stretch = Stretch.Uniform };
                cts.Token.ThrowIfCancellationRequested();
                PagesPanel.Children.Add(new Border
                {
                    Child = image,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                });
                if (PagesPanel.Children.Count == 1) FitWidth();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            Reset();
            ShowHint($"Preview failed: {ex.Message}");
            // Selecting the same fax again retries.
            _requestedKey = _shownKey = null;
        }
        finally
        {
            if (_load == cts) Ring.IsActive = false;
        }
    }

    /// <summary>Shows pages already in memory.</summary>
    public void ShowPages(string title, IReadOnlyList<FaxBitmap> pages) =>
        Show(Guid.NewGuid().ToString(), title, _ => Task.FromResult(pages));

    public void Clear(string? message = null) => Show(null, "", null, message);

    /// <summary>Shows an outgoing fax, from this PC or from the server.</summary>
    public void ShowFax(string title, OutboundFax fax) =>
        Show(fax.Id.ToString(), title, ct => App.GetService<IFaxData>().LoadOutboundPagesAsync(fax, ct));

    /// <summary>Shows a received fax, from this PC or from the server.</summary>
    public void ShowFax(string title, ReceivedFax fax) =>
        Show(fax.Id.ToString(), title, ct => App.GetService<IFaxData>().LoadReceivedPagesAsync(fax, ct));

    /// <summary>Adds the zoom shortcuts to <paramref name="scope"/> (the page or window hosting this pane).</summary>
    public void AddShortcutsTo(UIElement scope)
    {
        void Add(VirtualKey key, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, e) => { if (_pages.Count == 0) return; action(); e.Handled = true; };
            scope.KeyboardAccelerators.Add(accelerator);
        }
        Add(VirtualKey.Add, () => Zoom(ZoomStep));
        Add((VirtualKey)187, () => Zoom(ZoomStep)); // '=' / '+' key
        Add(VirtualKey.Subtract, () => Zoom(1 / ZoomStep));
        Add((VirtualKey)189, () => Zoom(1 / ZoomStep)); // '-' key
        Add(VirtualKey.Number0, FitWidth);
    }

    private void Reset()
    {
        PagesPanel.Children.Clear();
        _pages = [];
        _currentPage = 0;
        _autoFit = true;
        TitleText.Text = "";
        PageInfo.Text = "";
        Tools.IsEnabled = false;
        HintText.Visibility = Visibility.Collapsed;
        Scroller.ChangeView(0, 0, 1, disableAnimation: true);
    }

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;
    }

    private void Zoom(float factor)
    {
        _autoFit = false;
        Scroller.ChangeView(null, null, Math.Clamp(Scroller.ZoomFactor * factor, Scroller.MinZoomFactor, Scroller.MaxZoomFactor));
    }

    private void FitWidth()
    {
        _autoFit = true;
        if (Scroller.ViewportWidth <= 0) return;
        double content = PageWidth + PagesPanel.Padding.Left + PagesPanel.Padding.Right + 2;
        float zoom = (float)Math.Clamp(Scroller.ViewportWidth / content, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        Scroller.ChangeView(0, null, zoom, disableAnimation: true);
    }

    private void GoToPage(int index)
    {
        if (PagesPanel.Children.Count == 0) return;
        index = Math.Clamp(index, 0, PagesPanel.Children.Count - 1);
        // PageTop is in viewport coordinates (already zoomed and scrolled).
        double top = PageTop(PagesPanel.Children[index]) + Scroller.VerticalOffset - PagesPanel.Padding.Top * Scroller.ZoomFactor;
        Scroller.ChangeView(null, Math.Max(0, top), null);
        _currentPage = index;
        UpdatePageInfo();
    }

    private double PageTop(UIElement page) => page.TransformToVisual(Scroller).TransformPoint(new Point(0, 0)).Y;

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (PagesPanel.Children.Count == 0) return;
        // The current page is the last one whose top edge is above the upper third of the viewport.
        int current = 0;
        double line = Scroller.ViewportHeight / 3;
        for (int i = 0; i < PagesPanel.Children.Count; i++)
        {
            if (PageTop(PagesPanel.Children[i]) > line) break;
            current = i;
        }
        // At the bottom the last page may never reach that line.
        if (Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1) current = PagesPanel.Children.Count - 1;
        _currentPage = current;
        UpdatePageInfo();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_autoFit && PagesPanel.Children.Count > 0) FitWidth();
    }

    private void UpdatePageInfo() => PageInfo.Text = _pages.Count == 0 ? "" : $"Page {_currentPage + 1} of {_pages.Count}";

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / ZoomStep);
    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_currentPage + 1);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count > 0) PreviewWindow.Open(_title, _pages);
    }
}
