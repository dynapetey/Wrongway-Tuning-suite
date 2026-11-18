// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace WrongWay;

sealed partial class ScannerWindow
{
    private async Task OpenVcmControls()
    {
        var dialog = new Window
        {
            Title = "VCM control frame preview (P01 / P59 VPW)",
            Width = 680,
            Height = 560,
            MinWidth = 580,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BackgroundBrush,
            ShowInTaskbar = false
        };

        var logBox = new SelectableTextBlock
        {
            Text = "Offline preview · Select a control to inspect its draft VPW frame. Vehicle transmission requires verification.",
            Foreground = DeskUi.Ink,
            FontFamily = new FontFamily("DejaVu Sans Mono"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        void AppendLog(string line)
        {
            logBox.Text = $"[{DateTime.Now:HH:mm:ss}] {line}\n" + logBox.Text;
        }

        void PreviewControl(VcmControlRequest request)
        {
            try
            {
                string frame = Convert.ToHexString(VcmTelemetryControls.BuildVpwCommand(request));
                AppendLog($"Draft {request.Action}: {frame}");
            }
            catch (FormatException error)
            {
                AppendLog(error.Message);
            }
        }

        var idleInput = new TextBox { Text = "800", Width = 85, Watermark = "RPM" };
        var afrInput = new TextBox { Text = "14.7", Width = 85, Watermark = "AFR" };
        var sparkInput = new TextBox { Text = "22.0", Width = 85, Watermark = "Deg" };
        var cylChoice = new ComboBox
        {
            ItemsSource = new[]
            {
                "All Cylinders Active (Restore)",
                "Cut Cylinder 1", "Cut Cylinder 2", "Cut Cylinder 3", "Cut Cylinder 4",
                "Cut Cylinder 5", "Cut Cylinder 6", "Cut Cylinder 7", "Cut Cylinder 8"
            },
            SelectedIndex = 0,
            Width = 220
        };

        var specialSection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DeskUi.Section("SPECIAL FUNCTIONS & LEARNED ADAPTIVES"),
                Row(
                    Button("CASE Crank Variation Learn", () => PreviewControl(new(VcmControlAction.CrankRelearn))),
                    Button("Reset Fuel Trims", () => PreviewControl(new(VcmControlAction.ClearFuelTrims))),
                    Button("Clear DTCs", () => PreviewControl(new(VcmControlAction.ClearTroubleCodes))),
                    Button("Release All Controls", () => PreviewControl(new(VcmControlAction.ReleaseAllControls))))
            }
        };

        var relaySection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DeskUi.Section("COOLING FANS, FUEL PUMP & LOOP STATUS"),
                Row(
                    Button("Fan 1 ON", () => PreviewControl(new(VcmControlAction.Fan1On))),
                    Button("Fan 1 OFF", () => PreviewControl(new(VcmControlAction.Fan1Off))),
                    Button("Fan 2 ON", () => PreviewControl(new(VcmControlAction.Fan2On))),
                    Button("Fan 2 OFF", () => PreviewControl(new(VcmControlAction.Fan2Off)))),
                Row(
                    Button("Fuel Pump ON", () => PreviewControl(new(VcmControlAction.FuelPumpOn))),
                    Button("Fuel Pump OFF", () => PreviewControl(new(VcmControlAction.FuelPumpOff))),
                    Button("Force Open Loop", () => PreviewControl(new(VcmControlAction.ForceOpenLoop))),
                    Button("Force Closed Loop", () => PreviewControl(new(VcmControlAction.ForceClosedLoop))))
            }
        };

        var engineSection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DeskUi.Section("LIVE IDLE, AFR, SPARK & CYLINDER BALANCE OVERRIDES"),
                Row(
                    DeskUi.Label("Target Idle (400–2500 RPM):", 12, Muted), idleInput,
                    Button("Preview idle RPM", () =>
                    {
                        if (double.TryParse(idleInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double rpm))
                            PreviewControl(new(VcmControlAction.CommandIdleRpm, TargetValue: rpm));
                        else
                            AppendLog("Enter a valid numeric RPM.");
                    })),
                Row(
                    DeskUi.Label("Commanded AFR (10.0–18.0):", 12, Muted), afrInput,
                    Button("Preview AFR", () =>
                    {
                        if (double.TryParse(afrInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double afr))
                            PreviewControl(new(VcmControlAction.CommandAfr, TargetValue: afr));
                        else
                            AppendLog("Enter a valid numeric AFR.");
                    })),
                Row(
                    DeskUi.Label("Spark Advance (-10° to 45°):", 12, Muted), sparkInput,
                    Button("Preview spark", () =>
                    {
                        if (double.TryParse(sparkInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg))
                            PreviewControl(new(VcmControlAction.CommandSparkAdvance, TargetValue: deg));
                        else
                            AppendLog("Enter a valid numeric spark advance.");
                    })),
                Row(
                    DeskUi.Label("Cylinder Power Balance:", 12, Muted), cylChoice,
                    Button("Preview cylinder cut", () =>
                        PreviewControl(new(VcmControlAction.CylinderPowerBalance, CylinderIndex: Math.Max(0, cylChoice.SelectedIndex)))))
            }
        };

        var root = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            RowSpacing = 12
        };
        Put(root, DeskUi.FrameOf(specialSection, DeskUi.Surface, new Thickness(10)), 0);
        Put(root, DeskUi.FrameOf(relaySection, DeskUi.Surface, new Thickness(10)), 1);
        Put(root, DeskUi.FrameOf(engineSection, DeskUi.Surface, new Thickness(10)), 2);
        Put(root, DeskUi.FrameOf(new ScrollViewer
        {
            Content = logBox,
            Padding = new Thickness(10)
        }, DeskUi.ChartBackground), 3);

        var close = Button("Close", dialog.Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Put(root, close, 4);

        dialog.Content = root;
        await dialog.ShowDialog(this);
    }

    private async Task OpenTelemetryHub()
    {
        var dialog = new Window
        {
            Title = "VCM Telemetry · Sessions, Bundles & Live Stream",
            Width = 720,
            Height = 500,
            MinWidth = 600,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BackgroundBrush,
            ShowInTaskbar = false
        };

        var sessions = VcmTelemetryControls.LoadSessions().ToList();
        var list = new ListBox
        {
            ItemsSource = sessions.Select(FormatSession).ToArray(),
            SelectedIndex = sessions.Count > 0 ? 0 : -1
        };
        var info = DeskUi.Label(
            $"{sessions.Count} local telemetry session(s)",
            12, Muted);

        void RefreshList()
        {
            sessions = VcmTelemetryControls.LoadSessions().ToList();
            list.ItemsSource = sessions.Select(FormatSession).ToArray();
            list.SelectedIndex = sessions.Count > 0 ? 0 : -1;
            info.Text = $"{sessions.Count} local telemetry session(s)";
        }

        static string FormatSession(TelemetrySessionRecord s) =>
            $"{s.Name}   ·   {s.RecordedUtc.LocalDateTime:yyyy-MM-dd HH:mm}   ·   {s.Frames:N0} frames ({s.DurationSeconds:0.1} s)   ·   {s.Channels.Length} ch";

        var openSelected = Button("Open selected log", async () =>
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= sessions.Count)
                return;
            string path = sessions[idx].Path;
            if (!File.Exists(path))
            {
                info.Text = "File no longer exists at " + path;
                info.Foreground = Warning;
                return;
            }
            dialog.Close();
            await OpenLog(path);
        });

        var exportBundle = Button("Export Telemetry JSON Bundle…", async () =>
        {
            if (log is null)
            {
                info.Text = "Open or record a log first to export a telemetry bundle.";
                info.Foreground = Warning;
                return;
            }
            var save = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export VCM Telemetry Session Bundle",
                SuggestedFileName = Path.GetFileNameWithoutExtension(log.Name) + "-telemetry.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("VCM Telemetry JSON") { Patterns = ["*.json"] }]
            });
            if (save?.TryGetLocalPath() is { } target)
            {
                if (logPath is not null && Path.GetFullPath(logPath) == Path.GetFullPath(target))
                    throw new IOException("Choose a separate path for the telemetry bundle.");
                uint? sessionOs = sessions.FirstOrDefault(session => session.Path == logPath)?.OsId;
                VcmTelemetryControls.ExportTelemetryBundle(log, CurrentHistogramSnapshot(), sessionOs, target);
                info.Text = "Exported telemetry bundle to " + Path.GetFileName(target);
                info.Foreground = Accent;
            }
        });

        var removeSelected = Button("Remove from list", () =>
        {
            int idx = list.SelectedIndex;
            if (idx < 0 || idx >= sessions.Count)
                return;
            sessions.RemoveAt(idx);
            VcmTelemetryControls.SaveSessions(sessions.ToArray());
            RefreshList();
        });

        var layout = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 10
        };
        Put(layout, DeskUi.Section("LOCAL TELEMETRY SESSIONS"), 0);
        Put(layout, Row(openSelected, exportBundle, removeSelected), 1);
        Put(layout, list, 2);
        Put(layout, info, 3);
        var close = Button("Close", dialog.Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        Put(layout, close, 4);

        dialog.Content = layout;
        await dialog.ShowDialog(this);
    }
}
