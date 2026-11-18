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
        if (hardware is not null || busy || pcmToolsOpen)
        {
            SetStatus("Finish the current adapter operation before opening VCM Controls.", true);
            return;
        }
        var dialog = new Window
        {
            Title = "VCM Controls & Special Functions · OBDX Pro GT",
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
            Text = "Fan tests run for two seconds with the engine off and ignition on. Keep clear of the fans. " +
                "The log shows the PCM reply for each live command.",
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

        var liveButtons = new List<Button>();
        var stop = Button("Stop", () => hardware?.Cancel());
        stop.IsEnabled = false;
        dialog.Closing += (_, args) =>
        {
            if (!stop.IsEnabled)
                return;
            args.Cancel = true;
            hardware?.Cancel();
        };

        Button LiveButton(string label, VcmControlAction action)
        {
            var button = Button(label, async () =>
            {
                if (port.Text?.Trim() is not { Length: > 0 } device)
                {
                    AppendLog("Select the OBDX Pro GT serial port in the scanner first.");
                    return;
                }
                if (action == VcmControlAction.ClearTroubleCodes && !await ConfirmClearCodes(dialog))
                    return;

                foreach (var control in liveButtons)
                    control.IsEnabled = false;
                stop.IsEnabled = true;
                try
                {
                    AppendLog(label + " · connecting to " + device);
                    VcmControlResult? result = null;
                    bool success = await RunHardware(async token =>
                    {
                        result = await HammerdroidProbe.RunVcmControl(device, action, PostStatus, token);
                    }, recording: false, completion: label + " complete.");
                    if (success && result is not null)
                    {
                        IdentifyOs(result.OsId);
                        AppendLog($"PCM OS {result.OsId} accepted {label} · {result.Reply}");
                        if (result.ReleaseReply is { } release)
                            AppendLog($"Automatic fan control restored · {release}");
                    }
                    else
                        AppendLog(status.Text ?? "Adapter operation did not complete.");
                }
                finally
                {
                    stop.IsEnabled = false;
                    foreach (var control in liveButtons)
                        control.IsEnabled = true;
                }
            });
            liveButtons.Add(button);
            return button;
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
                DeskUi.Section("SPECIAL FUNCTIONS"),
                Row(
                    LiveButton("Clear DTCs…", VcmControlAction.ClearTroubleCodes),
                    Button("Preview CASE learn", () => PreviewControl(new(VcmControlAction.CrankRelearn))),
                    Button("Preview fuel trim reset", () => PreviewControl(new(VcmControlAction.ClearFuelTrims))))
            }
        };

        var relaySection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DeskUi.Section("COOLING FAN RELAY TESTS"),
                Row(
                    LiveButton("Fan 1 on", VcmControlAction.Fan1On),
                    LiveButton("Fan 1 off", VcmControlAction.Fan1Off),
                    LiveButton("Fan 2 on", VcmControlAction.Fan2On),
                    LiveButton("Fan 2 off", VcmControlAction.Fan2Off),
                    LiveButton("Return fans to PCM", VcmControlAction.ReleaseAllControls)),
                DeskUi.Section("FUEL SYSTEM FRAME PREVIEWS"),
                Row(
                    Button("Pump on", () => PreviewControl(new(VcmControlAction.FuelPumpOn))),
                    Button("Pump off", () => PreviewControl(new(VcmControlAction.FuelPumpOff))),
                    Button("Open loop", () => PreviewControl(new(VcmControlAction.ForceOpenLoop))),
                    Button("Closed loop", () => PreviewControl(new(VcmControlAction.ForceClosedLoop))))
            }
        };

        var engineSection = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DeskUi.Section("ENGINE CONTROL FRAME PREVIEWS"),
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
        var footer = Row(stop, close);
        footer.HorizontalAlignment = HorizontalAlignment.Right;
        Put(root, footer, 4);

        dialog.Content = root;
        await dialog.ShowDialog(this);
    }

    private static Task<bool> ConfirmClearCodes(Window owner)
    {
        var dialog = new Window
        {
            Title = "Clear diagnostic trouble codes?", Width = 410, Height = 155,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = BackgroundBrush, ShowInTaskbar = false, CanResize = false
        };
        var result = Button("Clear DTCs", () => dialog.Close(true));
        var cancel = Button("Cancel", () => dialog.Close(false));
        cancel.IsCancel = true;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(14), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "This clears stored emissions codes and resets readiness monitors.",
                    TextWrapping = TextWrapping.Wrap },
                Row(result, cancel)
            }
        };
        return dialog.ShowDialog<bool>(owner);
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
