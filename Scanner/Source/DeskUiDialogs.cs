using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace WrongWay;

static partial class DeskUi
{
    public static StackPanel Fields(params (string Label, Control Field)[] fields)
    {
        var panel = new StackPanel { Spacing = 5 };
        foreach (var (label, field) in fields)
        {
            panel.Children.Add(Label(label, 12, Muted));
            Avalonia.Automation.AutomationProperties.SetName(field, label);
            panel.Children.Add(field);
        }
        return panel;
    }

    private static Window LayoutDialog(string title, double width, double height) =>
        new() { Title = title, Width = width, Height = height, MinWidth = 390, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Frame, ShowInTaskbar = false };

    public static Task<T?> Form<T>(Window owner, string title, Control content, Func<T> read, double height = 540) where T : class
    {
        var dialog = LayoutDialog(title, 540, height);
        var error = new TextBlock { Foreground = Alert, TextWrapping = TextWrapping.Wrap };
        var apply = DialogButton("Apply", () =>
        {
            try { dialog.Close(read()); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or OverflowException) { error.Text = ex.Message; }
        });
        var cancel = DialogButton("Cancel", () => dialog.Close());
        cancel.IsCancel = true;
        var area = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10 };
        Put(area, new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 0);
        Put(area, error, 1);
        Put(area, Row(apply, cancel), 2);
        dialog.Content = area;
        return dialog.ShowDialog<T?>(owner);
    }

    public static Task<T[]?> EditList<T>(Window owner, string title, string description, T[] initial, int maximum,
        Func<Window, T?, Task<T?>> edit, Func<List<T>, string?>? validate = null) where T : class
    {
        var dialog = LayoutDialog(title, 650, 420);
        var items = new List<T>(initial);
        var list = new ListBox { ItemsSource = items.ToArray(), SelectedIndex = items.Count == 0 ? -1 : 0 };
        var error = new TextBlock { Foreground = Alert, TextWrapping = TextWrapping.Wrap };
        void Refresh(int index)
        {
            list.ItemsSource = items.ToArray();
            list.SelectedIndex = index;
            error!.Text = "";
        }
        void Move(int amount)
        {
            int index = list.SelectedIndex;
            int target = index + amount;
            if (index < 0 || target < 0 || target >= items.Count)
                return;
            (items[index], items[target]) = (items[target], items[index]);
            Refresh(target);
        }
        async Task Change(bool add)
        {
            int index = list.SelectedIndex;
            if (add && items.Count >= maximum)
            {
                error.Text = $"This panel supports up to {maximum} items.";
                return;
            }
            if (!add && index < 0)
                return;
            var result = await edit(dialog, add ? null : items[index]);
            if (result is null)
                return;
            if (add)
            {
                items.Add(result);
                index = items.Count - 1;
            }
            else
                items[index] = result;
            Refresh(index);
        }
        Button Command(string label, bool add)
        {
            var button = Tool(label);
            button.Click += async (_, _) =>
            {
                try { await Change(add); }
                catch (Exception ex) { error.Text = ex.Message; }
            };
            return button;
        }
        var remove = DialogButton("Remove", () =>
        {
            int index = list.SelectedIndex;
            if (index < 0)
                return;
            items.RemoveAt(index);
            Refresh(Math.Min(index, items.Count - 1));
        });
        var apply = DialogButton("Apply", () =>
        {
            string? message = validate?.Invoke(items);
            if (message is null)
                dialog.Close(items.ToArray());
            else
                error.Text = message;
        });
        var cancel = DialogButton("Cancel", () => dialog.Close());
        cancel.IsCancel = true;
        var area = new Grid { Margin = new Thickness(14), RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 8 };
        Put(area, new TextBlock { Text = description, Foreground = Muted, TextWrapping = TextWrapping.Wrap }, 0);
        Put(area, Row(Command("Add", true), Command("Edit", false), remove,
            DialogButton("Move up", () => Move(-1)), DialogButton("Move down", () => Move(1))), 1);
        Put(area, list, 2);
        Put(area, error, 3);
        Put(area, Row(apply, cancel), 4);
        dialog.Content = area;
        return dialog.ShowDialog<T[]?>(owner);
    }
    private static Button DialogButton(string label, Action action)
    {
        var button = Tool(label);
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var child in children)
            row.Children.Add(child);
        return row;
    }

    private static void Put(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
