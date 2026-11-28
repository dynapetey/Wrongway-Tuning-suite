using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WrongWayScanner.Telemetry;

namespace WrongWayScanner.UI
{
    public sealed class TelemetryChannelRow
    {
        public string Channel { get; set; } = string.Empty;
        public string Value { get; set; } = "--";
        public string Units { get; set; } = string.Empty;
    }

    public sealed class ScannerReadoutBinder : IDisposable
    {
        private readonly ScannerTelemetryController _controller = new();
        private readonly Action<Action> _marshalToUiThread;

        public ObservableCollection<TelemetryChannelRow> Channels { get; } = new()
        {
            new TelemetryChannelRow { Channel = "RPM", Units = "RPM" },
            new TelemetryChannelRow { Channel = "DynCylAir", Units = "g/cyl" },
            new TelemetryChannelRow { Channel = "MAP_kPa", Units = "kPa" },
            new TelemetryChannelRow { Channel = "KnockRetard", Units = "deg" },
            new TelemetryChannelRow { Channel = "InjPW_B1", Units = "ms" },
            new TelemetryChannelRow { Channel = "IDC_Pct", Units = "%" },
            new TelemetryChannelRow { Channel = "CmdEQ", Units = "EQ" },
            new TelemetryChannelRow { Channel = "SparkAdv", Units = "deg" },
            new TelemetryChannelRow { Channel = "TPS", Units = "%" },
            new TelemetryChannelRow { Channel = "TCC_Slip", Units = "RPM" },
            new TelemetryChannelRow { Channel = "TFT_C", Units = "C" },
            new TelemetryChannelRow { Channel = "CmdGear", Units = "Gear" }
        };

        public event Action<bool, string>? StatusChanged;

        public ScannerReadoutBinder(Action<Action> marshalToUiThread)
        {
            _marshalToUiThread = marshalToUiThread ?? throw new ArgumentNullException(nameof(marshalToUiThread));
            _controller.TelemetryReceived += OnTelemetryReceived;
            _controller.ConnectionStateChanged += (connected, msg) =>
                _marshalToUiThread(() => StatusChanged?.Invoke(connected, msg));
        }

        public async Task ToggleConnectionAsync(string selectedLayout, string? portOrDllOverride = null)
        {
            if (_controller.IsConnected)
            {
                _controller.Disconnect();
                return;
            }

            try
            {
                StatusChanged?.Invoke(false, "Opening OBDX Pro GT (J1850 VPW)...");
                await _controller.ConnectAsync(selectedLayout, portOrDllOverride);
            }
            catch (Exception ex)
            {
                _controller.Disconnect();
                StatusChanged?.Invoke(false, $"Connection failed: {ex.Message}");
            }
        }

        public async Task OnLayoutSelectionChangedAsync(string newLayout)
        {
            if (!_controller.IsConnected)
                return;

            try
            {
                await _controller.SwitchLayoutAsync(newLayout);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(false, $"Layout switch failed: {ex.Message}");
            }
        }

        private void OnTelemetryReceived(IReadOnlyDictionary<string, double> frame)
        {
            _marshalToUiThread(() =>
            {
                foreach (var row in Channels)
                {
                    if (frame.TryGetValue(row.Channel, out double val))
                    {
                        row.Value = row.Channel switch
                        {
                            "DynCylAir" or "CmdEQ" => val.ToString("F3"),
                            "InjPW_B1" or "KnockRetard" => val.ToString("F2"),
                            "IDC_Pct" or "SparkAdv" or "TPS" => val.ToString("F1"),
                            _ => val.ToString("F0")
                        };
                    }
                }
            });
        }

        public void Dispose() => _controller.Dispose();
    }
}
