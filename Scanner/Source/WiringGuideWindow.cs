using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace WrongWay;

sealed class WiringGuideWindow : Window
{
    private const string PdfName = "2006-LM7-to-1995-F150-Fuse-Block.pdf";
    private static readonly string[] Pages =
    [
        "1 · Power distribution",
        "2 · Grounds, starter and fuel pump",
        "3 · Ford-to-GM signal splices",
        "4 · Ford C101 and C103 connector faces",
        "5 · Ford C202 and cab connectors",
        "6 · GM pigtails and sensor returns",
        "7 · 4L60E and drive-by-wire",
        "8 · PCM, diagnostic port and accessories",
        "9 · Ford PCM, fuse box and switches"
    ];

    private readonly Image image = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer viewport;
    private readonly TextBlock status = DeskUi.Label("", 12, DeskUi.Muted);
    private Bitmap? page;

    public WiringGuideWindow()
    {
        Title = "2006 LM7 to ’95 F-150 wiring guide";
        Width = 1080;
        Height = 750;
        MinWidth = 680;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = DeskUi.Frame;
        ShowInTaskbar = false;

        var selector = new ComboBox { ItemsSource = Pages, MinWidth = 260 };
        Avalonia.Automation.AutomationProperties.SetName(selector, "Wiring guide page");
        var previous = DeskUi.Tool("Previous");
        var next = DeskUi.Tool("Next");
        var zoom = new ToggleButton { Content = "Full size", VerticalAlignment = VerticalAlignment.Center };
        var save = DeskUi.Tool("Save PDF…", DeskUi.Save);
        save.IsEnabled = StorageProvider.CanSave;
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8),
            Children = { previous, next, selector, zoom, save }
        };
        viewport = new ScrollViewer
        {
            Content = image,
            Padding = new Thickness(12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = DeskUi.Surface
        };
        status.Margin = new Thickness(10, 5);
        status.IsVisible = false;
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        Content = new DockPanel { Children = { toolbar, status, viewport } };

        previous.Click += (_, _) => selector.SelectedIndex--;
        next.Click += (_, _) => selector.SelectedIndex++;
        selector.SelectionChanged += (_, _) =>
        {
            int index = selector.SelectedIndex;
            if (index < 0)
                return;
            using var content = OpenResource($"page-{index + 1}.png");
            var nextPage = new Bitmap(content);
            var oldPage = page;
            image.Source = page = nextPage;
            oldPage?.Dispose();
            SizePage(zoom.IsChecked == true);
            viewport.Offset = default;
            previous.IsEnabled = index > 0;
            next.IsEnabled = index < Pages.Length - 1;
        };
        viewport.SizeChanged += (_, _) => SizePage(zoom.IsChecked == true);
        zoom.IsCheckedChanged += (_, _) =>
        {
            bool fullSize = zoom.IsChecked == true;
            viewport.HorizontalScrollBarVisibility = fullSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            SizePage(fullSize);
        };
        save.Click += async (_, _) => await SavePdfAsync(save);
        Closed += (_, _) =>
        {
            image.Source = null;
            page?.Dispose();
        };
        selector.SelectedIndex = 0;
    }

    private void SizePage(bool fullSize)
    {
        if (page is null)
            return;
        double width = fullSize ? page.Size.Width : Math.Max(1, viewport.Bounds.Width - 44);
        image.Width = width;
        image.Height = width * page.Size.Height / page.Size.Width;
    }

    private async Task SavePdfAsync(Button save)
    {
        save.IsEnabled = false;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save wiring guide PDF",
                SuggestedFileName = PdfName,
                DefaultExtension = "pdf",
                FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }]
            });
            if (file is null)
                return;
            using var source = OpenResource(PdfName);
            await using (var destination = await file.OpenWriteAsync())
            {
                if (destination.CanSeek)
                    destination.SetLength(0);
                await source.CopyToAsync(destination);
            }
            status.Foreground = DeskUi.Muted;
            status.Text = $"Saved {file.Name}.";
            status.IsVisible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            status.Foreground = DeskUi.Alert;
            status.Text = $"Could not save the PDF. {ex.Message}";
            status.IsVisible = true;
        }
        finally
        {
            save.IsEnabled = true;
        }
    }

    private static Stream OpenResource(string name) =>
        typeof(WiringGuideWindow).Assembly.GetManifestResourceStream($"WrongWay.WiringGuide.{name}")
            ?? throw new InvalidOperationException($"Missing bundled wiring guide file {name}.");
}
