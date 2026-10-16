using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GuiaFotos.Core;

namespace GuiaFotos.Windows;

// Virtualize rows, rather than creating an Image control for every photo in a folder.
public sealed class PhotoGrid : UserControl
{
    private readonly ListBox rows = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, Tile> tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Row> layout = [];
    private List<MediaItem> items = [];
    private string? active, anchor;
    private bool updating;
    private int columns = 1;
    private double thumbnailSize = 164;
    public SelectionCollection SelectedItems { get; } = new();
    public MediaItem? SelectedItem => active != null && SelectedItems.FirstOrDefault(p => Same(p.Path, active)) is { } item ? item : SelectedItems.LastOrDefault();
    public IReadOnlyList<MediaItem> Items => items;
    public event SelectionChangedEventHandler? SelectionChanged;
    public event Action<MediaItem>? PhotoOpened;
    public event Action? DeleteRequested;
    public event Action<double>? ZoomRequested;
    public IEnumerable<MediaItem> ItemsSource { get => items; set => SetItems(value); }
    public double ThumbnailSize { get => thumbnailSize; set { var top = TopPhoto(); thumbnailSize = Math.Clamp(value, 96, 280); Reflow(top); } }
    public bool ShowNames { get; set; } = true;

    public PhotoGrid()
    {
        Focusable = true; Content = rows;
        ScrollViewer.SetCanContentScroll(rows, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(rows, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(rows, true);
        VirtualizingPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(rows, ScrollUnit.Pixel);
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        container.Setters.Add(new Setter(FrameworkElement.FocusableProperty, false));
        container.Setters.Add(new Setter(FrameworkElement.HeightProperty, new Binding(nameof(Row.Height))));
        var template = new ControlTemplate(typeof(ListBoxItem));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        container.Setters.Add(new Setter(Control.TemplateProperty, template)); rows.ItemContainerStyle = container;
        rows.ItemTemplateSelector = new RowTemplates();
        SelectedItems.CollectionChanged += (_, _) => SelectionUpdated();
        SizeChanged += (_, _) => { var next = Math.Max(1, (int)((ActualWidth - 28) / (thumbnailSize + 18))); if (next != columns) Reflow(TopPhoto()); };
        PreviewKeyDown += Keyboard;
        PreviewMouseWheel += (_, e) => { if (KeyboardModifiers().HasFlag(ModifierKeys.Control)) { ZoomRequested?.Invoke(e.Delta > 0 ? 16 : -16); e.Handled = true; } };
    }
    private static bool Same(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);
    private static ModifierKeys KeyboardModifiers() => System.Windows.Input.Keyboard.Modifiers;
    private void SetItems(IEnumerable<MediaItem>? source)
    {
        var top = TopPhoto(); var selected = SelectedItems.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        items = (source ?? []).GroupBy(p => Path.GetDirectoryName(p.Path) ?? "").OrderByDescending(g => g.Max(p => p.Taken)).SelectMany(g => g.OrderByDescending(p => p.Taken).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)).ToList(); tiles.Clear();
        foreach (var item in items) tiles[item.Path] = new Tile(this, item, thumbnailSize, selected.Contains(item.Path));
        if (anchor != null && !tiles.ContainsKey(anchor)) anchor = null;
        if (active != null && !tiles.ContainsKey(active)) active = null;
        updating = true; SelectedItems.Replace(items.Where(p => selected.Contains(p.Path))); updating = false;
        Reflow(top);
    }
    private void Reflow(string? top)
    {
        columns = Math.Max(1, (int)((ActualWidth - 28) / (thumbnailSize + 18))); layout.Clear();
        foreach (var group in items.GroupBy(p => Path.GetDirectoryName(p.Path) ?? "").OrderByDescending(g => g.Max(p => p.Taken)))
        {
            var ordered = group.OrderByDescending(p => p.Taken).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var range = ordered.Min(p => p.Taken); var end = ordered.Max(p => p.Taken);
            var name = Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar));
            layout.Add(new Row(name.Length > 0 ? name : group.Key, $"{group.Count()} {(group.Count() == 1 ? "item" : "itens")}  ·  {range:dd/MM/yyyy}" + (range.Date != end.Date ? $" – {end:dd/MM/yyyy}" : ""), group.Key, [], 64));
            foreach (var batch in ordered.Chunk(columns))
            {
                var rowTiles = batch.Select(p => tiles[p.Path]).ToArray();
                foreach (var tile in rowTiles) tile.Resize(thumbnailSize, ShowNames);
                layout.Add(new Row("", "", "", rowTiles, thumbnailSize * .76 + (ShowNames ? 48 : 26)));
            }
        }
        rows.ItemsSource = layout.ToArray();
        if (top != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollTo(top));
    }
    public void RefreshNames() => Reflow(TopPhoto());
    public void Select(MediaItem item, bool extend = false, bool toggle = false)
    {
        var previous = SelectedItems.ToArray(); active = item.Path;
        if (extend && anchor != null)
        {
            var a = items.FindIndex(p => Same(p.Path, anchor)); var b = items.FindIndex(p => Same(p.Path, item.Path));
            if (a >= 0 && b >= 0) SelectedItems.Replace(items.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else if (toggle) { if (SelectedItems.FirstOrDefault(p => Same(p.Path, item.Path)) is { } found) SelectedItems.Remove(found); else SelectedItems.Add(item); anchor = item.Path; }
        else { SelectedItems.Replace([item]); anchor = item.Path; }
        Focus();
    }
    private void SelectionUpdated()
    {
        if (updating) return;
        var selected = SelectedItems.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in tiles.Values) tile.Selected = selected.Contains(tile.Item.Path);
        SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), SelectedItems.ToArray()));
    }
    private void Keyboard(object sender, KeyEventArgs e)
    {
        var modifiers = KeyboardModifiers();
        if (e.Key == Key.A && modifiers.HasFlag(ModifierKeys.Control)) { SelectedItems.Replace(items); e.Handled = true; return; }
        if (e.Key == Key.Enter && SelectedItem is { } photo) { PhotoOpened?.Invoke(photo); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteRequested?.Invoke(); e.Handled = true; return; }
        var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -columns, Key.Down => columns, Key.Home => -items.Count, Key.End => items.Count, _ => 0 };
        if (delta == 0 || items.Count == 0) return;
        var index = Math.Max(0, items.FindIndex(p => active != null && Same(p.Path, active)));
        var next = items[Math.Clamp(index + delta, 0, items.Count - 1)];
        Select(next, modifiers.HasFlag(ModifierKeys.Shift)); ScrollTo(next.Path); e.Handled = true;
    }
    public void ScrollTo(string path)
    {
        double offset = 0;
        for (var i = 0; i < layout.Count; i++)
        {
            var row = layout[i];
            if (row.Tiles.Any(p => Same(p.Item.Path, path))) { if (i > 0 && layout[i - 1].Tiles.Count == 0) offset -= layout[i - 1].Height; FindScroll(rows)?.ScrollToVerticalOffset(offset); return; }
            offset += row.Height;
        }
    }
    private string? TopPhoto()
    {
        var offset = FindScroll(rows)?.VerticalOffset ?? 0; double y = 0;
        foreach (var row in layout) { y += row.Height; if (y > offset && row.Tiles.Count > 0) return row.Tiles[0].Item.Path; }
        return null;
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } result) return result;
        return null;
    }
    public int RealizedRows => Enumerable.Range(0, rows.Items.Count).Count(i => rows.ItemContainerGenerator.ContainerFromIndex(i) != null);
    internal bool IsPhotoVisible(string path)
    {
        return FindTile(rows);
        bool FindTile(DependencyObject parent)
        {
            if (parent is PhotoTile tile && tile.DataContext is Tile data && Same(data.Item.Path, path))
            {
                var bounds = tile.TransformToAncestor(this).TransformBounds(new Rect(tile.RenderSize));
                return tile.IsVisible && tile.HasImage && bounds.IntersectsWith(new Rect(RenderSize));
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindTile(VisualTreeHelper.GetChild(parent, i))) return true;
            return false;
        }
    }
    public sealed class SelectionCollection : ObservableCollection<MediaItem>
    {
        public void Replace(IEnumerable<MediaItem> source) { var copy = source.ToArray(); Items.Clear(); foreach (var p in copy) Items.Add(p); OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count))); OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); }
    }
    public sealed record Row(string Title, string Caption, string Folder, IReadOnlyList<Tile> Tiles, double Height);
    public sealed class Tile(PhotoGrid owner, MediaItem item, double size, bool selected) : INotifyPropertyChanged
    {
        public MediaItem Item { get; } = item;
        public PhotoGrid Owner { get; } = owner;
        public string Thumbnail => Item.Thumbnail;
        public string Name => Item.Name;
        public string Tooltip => Item.Path + "\n" + Item.Details;
        public double Width { get; private set; } = size;
        public double ImageHeight => Width * .76;
        public Visibility NameVisibility { get; private set; } = Visibility.Visible;
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Resize(double value, bool names) { Width = value; NameVisibility = names ? Visibility.Visible : Visibility.Collapsed; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    }
    private sealed class RowTemplates : DataTemplateSelector
    {
        private readonly DataTemplate header = MakeHeader();
        private readonly DataTemplate photos = MakePhotos();
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => ((Row)item).Tiles.Count == 0 ? header : photos;
        private static DataTemplate MakeHeader()
        {
            var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(FrameworkElement.HeightProperty, 64d); panel.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 10, 0));
            var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Title))); title.SetValue(TextBlock.FontSizeProperty, 19d); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 12, 0, 3)); panel.AppendChild(title);
            var caption = new FrameworkElementFactory(typeof(TextBlock)); caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Caption))); caption.SetValue(TextBlock.ForegroundProperty, Brushes.DimGray); panel.AppendChild(caption);
            return new DataTemplate(typeof(Row)) { VisualTree = panel };
        }
        private static DataTemplate MakePhotos()
        {
            var list = new FrameworkElementFactory(typeof(ItemsControl)); list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(Row.Tiles))); list.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 0, 0, 0));
            var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); list.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));
            var tile = new FrameworkElementFactory(typeof(PhotoTile)); list.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate(typeof(Tile)) { VisualTree = tile });
            return new DataTemplate(typeof(Row)) { VisualTree = list };
        }
    }
    public sealed class PhotoTile : Border
    {
        private readonly Image image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        private readonly TextBlock name = new() { TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(1, 5, 1, 0), Foreground = new SolidColorBrush(Color.FromRgb(68, 72, 78)) };
        private Tile? tile;
        internal bool HasImage => image.Source != null && image.ActualWidth > 0 && image.ActualHeight > 0;
        public PhotoTile()
        {
            Margin = new Thickness(7, 3, 9, 7); Padding = new Thickness(4); BorderThickness = new Thickness(2); Cursor = Cursors.Hand; CornerRadius = new CornerRadius(2);
            var panel = new StackPanel(); panel.Children.Add(image); panel.Children.Add(name); Child = panel;
            DataContextChanged += (_, _) => { if (tile != null) tile.PropertyChanged -= Changed; tile = DataContext as Tile; if (tile == null) return; tile.PropertyChanged += Changed; Width = tile.Width; image.Height = tile.ImageHeight; name.Text = tile.Name; name.Visibility = tile.NameVisibility; ToolTip = tile.Tooltip; image.SetBinding(Image.SourceProperty, new Binding(nameof(Tile.Thumbnail))); Paint(); };
            PreviewMouseLeftButtonDown += (_, e) => { if (tile == null) return; var keys = KeyboardModifiers(); tile.Owner.Select(tile.Item, keys.HasFlag(ModifierKeys.Shift), keys.HasFlag(ModifierKeys.Control)); if (e.ClickCount == 2) tile.Owner.PhotoOpened?.Invoke(tile.Item); e.Handled = true; };
        }
        private void Changed(object? sender, PropertyChangedEventArgs e) { if (tile != null) { Width = tile.Width; image.Height = tile.ImageHeight; name.Visibility = tile.NameVisibility; } Paint(); }
        private void Paint() { BorderBrush = tile?.Selected == true ? new SolidColorBrush(Color.FromRgb(62, 131, 186)) : Brushes.Transparent; Background = tile?.Selected == true ? new SolidColorBrush(Color.FromRgb(222, 237, 249)) : Brushes.Transparent; }
    }
}
