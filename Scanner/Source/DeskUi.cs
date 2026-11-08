using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace WrongWay;

static class DeskUi
{
    public static readonly IBrush Frame = Brush.Parse("#181D26");
    public static readonly IBrush Surface = Brush.Parse("#12161E");
    public static readonly IBrush Edge = Brush.Parse("#2C3545");
    public static readonly IBrush Header = Brush.Parse("#1C2431");
    public static readonly IBrush Ink = Brush.Parse("#E0E8F4");
    public static readonly IBrush Muted = Brush.Parse("#A9B7CD");
    public static readonly IBrush Blue = Brush.Parse("#639DFF");
    public static readonly IBrush Alert = Brush.Parse("#FFAA99");
    public static readonly IBrush ChartBackground = Brush.Parse("#10131A");
    public static readonly IBrush ChartMuted = Muted;

    public static TextBlock Label(string text, double size = 12, IBrush? brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Ink,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static Border FrameOf(Control content, IBrush? background = null, Thickness? padding = null) => new()
    {
        Child = content, Background = background ?? Surface, BorderBrush = Edge,
        BorderThickness = new Thickness(1), Padding = padding ?? default
    };

    public static Border Strip(Control content, IBrush? background = null) => new()
    {
        Child = content, Background = background ?? Frame, BorderBrush = Edge,
        BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 3)
    };

    public static TextBlock Section(string text)
    {
        TextBlock label = Label(text, 12, Blue);
        label.FontWeight = FontWeight.SemiBold;
        return label;
    }

    public static GridSplitter Splitter(bool horizontal) => new()
    {
        Background = Frame,
        ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };

    public static Button Tool(string label, string? glyph = null)
    {
        Control content = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null)
        {
            var icon = new PathIcon
            {
                Data = Geometry.Parse(glyph), Width = 14, Height = 14, Foreground = Blue,
                VerticalAlignment = VerticalAlignment.Center
            };
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5, Children = { icon, content }
            };
        }
        var button = new Button
        {
            Content = content, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, MinHeight = 27
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    public static MenuItem Action(string label, Button button)
    {
        var item = new MenuItem { Header = label };
        item.Bind(MenuItem.IsEnabledProperty, new Binding(nameof(Button.IsEnabled)) { Source = button });
        item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return item;
    }

    public static MenuItem Menu(string label, params MenuItem[] items) => new() { Header = label, ItemsSource = items };
    public const string Open = "M1,4 L6,4 L8,6 L15,6 L13,14 L1,14 Z M3,2 L7,2 L9,4 L14,4 L14,5 L8,5 L6,3 L3,3 Z";
    public const string Save = "M2,1 H12 L15,4 V15 H2 Z M4,2 V6 H11 V2 Z M4,9 V14 H13 V9 Z";
    public const string Record = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z";
    public const string Stop = "M3,3 H13 V13 H3 Z";
    public const string Chart = "M2,2 H3 V13 H15 V14 H2 Z M4,10 L7,6 L10,8 L14,3 L15,4 L10,10 L7,8 L5,11 Z";
    public const string Table = "M1,2 H15 V14 H1 Z M2,5 V8 H6 V5 Z M7,5 V8 H10 V5 Z M11,5 V8 H14 V5 Z M2,9 V13 H6 V9 Z M7,9 V13 H10 V9 Z M11,9 V13 H14 V9 Z";
}
