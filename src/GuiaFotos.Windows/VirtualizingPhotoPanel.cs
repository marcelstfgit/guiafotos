using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.Specialized;

namespace GuiaFotos.Windows;

public sealed class VirtualizingPhotoPanel : VirtualizingPanel, IScrollInfo
{
    public double ItemWidth { get; set; } = 180;
    public double ItemHeight { get; set; } = 184;
    private Size extent, viewport;
    private double offset;
    private int columns = 1;
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this); if (owner == null) return available;
        var width = double.IsInfinity(available.Width) ? Math.Max(1, ActualWidth) : Math.Max(1, available.Width);
        var height = double.IsInfinity(available.Height) ? Math.Max(ItemHeight, ActualHeight) : Math.Max(1, available.Height);
        columns = Math.Max(1, (int)(width / ItemWidth)); viewport = new Size(width, height); extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * ItemHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height)); ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Min(owner.Items.Count, Math.Max(0, (int)(offset / ItemHeight) * columns));
        var last = Math.Min(owner.Items.Count - 1, ((int)((offset + height) / ItemHeight) + 2) * columns - 1);
        var generator = ItemContainerGenerator; var position = generator.GeneratorPositionFromIndex(first); var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew) { if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child); generator.PrepareItemContainer(child); }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var p = new GeneratorPosition(i, 0); var index = generator.IndexFromGeneratorPosition(p);
            if (index < first || index > last) { if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(p, 1); else generator.Remove(p, 1); RemoveInternalChildRange(i, 1); }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % columns * ItemWidth, index / columns * ItemHeight - offset, ItemWidth, ItemHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action == NotifyCollectionChangedAction.Reset) RemoveInternalChildRange(0, InternalChildren.Count);
        else if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        else if (args.Action == NotifyCollectionChangedAction.Move) RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index) { SetVerticalOffset(index / Math.Max(1, columns) * ItemHeight); UpdateLayout(); }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double value) { }
    public void SetVerticalOffset(double value) { offset = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height)); InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); }
    public void LineUp() => SetVerticalOffset(offset - 30);
    public void LineDown() => SetVerticalOffset(offset + 30);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelUp() => SetVerticalOffset(offset - ItemHeight * .7);
    public void MouseWheelDown() => SetVerticalOffset(offset + ItemHeight * .7);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual; while (child != null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is UIElement element && InternalChildren.IndexOf(element) is var position && position >= 0)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(position, 0)); var y = index / columns * ItemHeight;
            if (y < offset) SetVerticalOffset(y); else if (y + ItemHeight > offset + viewport.Height) SetVerticalOffset(y + ItemHeight - viewport.Height);
        }
        return rectangle;
    }
}
