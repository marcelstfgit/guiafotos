using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GuiaFotos.Windows;

public sealed class PhotoCanvas : Grid
{
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent };
    private readonly Rectangle crop = new() { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), Visibility = Visibility.Collapsed };
    private Point? start;
    private Rect? selection;
    private bool cropMode;
    public ImageSource? Source { get => image.Source; set { image.Source = value; ClearCrop(); } }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public bool CropMode { get => cropMode; set { cropMode = value; Cursor = value ? Cursors.Cross : Cursors.Arrow; if (!value) ClearCrop(); } }
    public event Action? CropChanged;
    public string? CropGeometry
    {
        get
        {
            if (selection is not { } rect || OriginalWidth < 1 || OriginalHeight < 1) return null;
            var bounds = ImageBounds();
            var x = Math.Clamp((int)Math.Round((rect.X - bounds.X) / bounds.Width * OriginalWidth), 0, OriginalWidth - 1);
            var y = Math.Clamp((int)Math.Round((rect.Y - bounds.Y) / bounds.Height * OriginalHeight), 0, OriginalHeight - 1);
            var width = Math.Clamp((int)Math.Round(rect.Width / bounds.Width * OriginalWidth), 1, OriginalWidth - x);
            var height = Math.Clamp((int)Math.Round(rect.Height / bounds.Height * OriginalHeight), 1, OriginalHeight - y);
            return $"{width}x{height}+{x}+{y}";
        }
    }
    public PhotoCanvas()
    {
        Background = new SolidColorBrush(Color.FromRgb(42, 44, 47)); ClipToBounds = true;
        Children.Add(image); Children.Add(overlay); overlay.Children.Add(crop);
        overlay.MouseLeftButtonDown += (_, e) => { if (!CropMode || Source == null) return; start = Clamp(e.GetPosition(this)); selection = null; overlay.CaptureMouse(); e.Handled = true; };
        overlay.MouseMove += (_, e) => { if (start is not { } point || !overlay.IsMouseCaptured) return; var end = Clamp(e.GetPosition(this)); SetCrop(new Rect(point, end)); e.Handled = true; };
        overlay.MouseLeftButtonUp += (_, e) => { if (start == null) return; start = null; overlay.ReleaseMouseCapture(); if (selection is { Width: < 5 } or { Height: < 5 }) ClearCrop(); CropChanged?.Invoke(); e.Handled = true; };
        SizeChanged += (_, _) => ClearCrop();
    }
    private Rect ImageBounds()
    {
        var bitmap = Source as BitmapSource;
        var width = bitmap?.PixelWidth ?? OriginalWidth; var height = bitmap?.PixelHeight ?? OriginalHeight;
        if (width < 1 || height < 1 || ActualWidth < 1 || ActualHeight < 1) return new Rect(0, 0, 1, 1);
        var scale = Math.Min(ActualWidth / width, ActualHeight / height);
        return new Rect((ActualWidth - width * scale) / 2, (ActualHeight - height * scale) / 2, width * scale, height * scale);
    }
    private Point Clamp(Point point) { var bounds = ImageBounds(); return new Point(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom)); }
    private void SetCrop(Rect rect) { selection = rect; Canvas.SetLeft(crop, rect.X); Canvas.SetTop(crop, rect.Y); crop.Width = rect.Width; crop.Height = rect.Height; crop.Visibility = Visibility.Visible; CropChanged?.Invoke(); }
    public void SetNormalizedCrop(Rect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 || rect.Right > 1 || rect.Bottom > 1) throw new ArgumentOutOfRangeException(nameof(rect));
        var bounds = ImageBounds(); SetCrop(new Rect(bounds.X + rect.X * bounds.Width, bounds.Y + rect.Y * bounds.Height, rect.Width * bounds.Width, rect.Height * bounds.Height));
    }
    public void ClearCrop() { start = null; if (overlay.IsMouseCaptured) overlay.ReleaseMouseCapture(); selection = null; crop.Visibility = Visibility.Collapsed; CropChanged?.Invoke(); }
}
