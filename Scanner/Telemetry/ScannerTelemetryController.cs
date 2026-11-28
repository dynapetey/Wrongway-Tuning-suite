using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WrongWayScanner.Telemetry.J2534;

namespace WrongWayScanner.Telemetry
{
    public sealed class ScannerTelemetryController : IDisposable
    {
        private IJ2534Transport? _transport;
        private VpwDdpSession? _session;
        private readonly object _lock = new();
        private long _lastUiDispatchTicks;
        private static readonly long MinUiIntervalTicks = TimeSpan.FromMilliseconds(40).Ticks;

        public event Action<IReadOnlyDictionary<string, double>>? TelemetryReceived;
        public event Action<bool, string>? ConnectionStateChanged;

        public bool IsConnected => _session?.IsStreaming ?? false;

        public async Task ConnectAsync(string layoutName = "SpeedDensity", string? explicitPortOrDll = null)
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    DisconnectInternal();

                    _transport = ObdxTransportFactory.CreateAuto(explicitPortOrDll);
                    _session = new VpwDdpSession(_transport);
                    _session.FrameUpdated += OnSessionFrameUpdated;

                    var presets = P59DdpPresetFactory.GetDdpSlotsForLayout(layoutName);
                    _session.Start(presets, DdpStreamRate.Fast);
                }
            });

            ConnectionStateChanged?.Invoke(true, $"Connected ({layoutName} - VPW Mode $2A Fast)");
        }

        public async Task SwitchLayoutAsync(string layoutName)
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_session == null)
                        throw new InvalidOperationException("Not connected to OBDX Pro GT.");

                    var presets = P59DdpPresetFactory.GetDdpSlotsForLayout(layoutName);
                    _session.Start(presets, DdpStreamRate.Fast);
                }
            });

            ConnectionStateChanged?.Invoke(true, $"Streaming {layoutName}");
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                DisconnectInternal();
            }
            ConnectionStateChanged?.Invoke(false, "Disconnected");
        }

        private void DisconnectInternal()
        {
            if (_session != null)
            {
                _session.FrameUpdated -= OnSessionFrameUpdated;
                _session.Dispose();
                _session = null;
            }

            if (_transport != null)
            {
                _transport.Dispose();
                _transport = null;
            }
        }

        private void OnSessionFrameUpdated(IReadOnlyDictionary<string, double> frame)
        {
            long now = DateTime.UtcNow.Ticks;
            long prev = Interlocked.Read(ref _lastUiDispatchTicks);
            if (now - prev < MinUiIntervalTicks)
                return;

            if (Interlocked.CompareExchange(ref _lastUiDispatchTicks, now, prev) == prev)
            {
                TelemetryReceived?.Invoke(frame);
            }
        }

        public void Dispose() => Disconnect();
    }
}
