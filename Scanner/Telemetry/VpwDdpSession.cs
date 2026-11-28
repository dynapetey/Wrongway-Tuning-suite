using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WrongWayScanner.Telemetry.J2534;

namespace WrongWayScanner.Telemetry
{
    public sealed class VpwDdpSession : IDisposable
    {
        private readonly VpwDdpManager _ddpManager;
        private readonly IJ2534Transport _transport;
        private readonly ConcurrentDictionary<string, double> _latestFrame = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _cts;
        private Task? _readerTask;

        public event Action<IReadOnlyDictionary<string, double>>? FrameUpdated;
        public bool IsStreaming => _readerTask is { IsCompleted: false };
        public IReadOnlyDictionary<string, double> LatestFrame => _latestFrame;

        public VpwDdpSession(IJ2534Transport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _ddpManager = new VpwDdpManager(transport);
        }

        public void Start(IEnumerable<DdpSlotConfiguration> slotPresets, DdpStreamRate rate = DdpStreamRate.Fast)
        {
            if (IsStreaming)
                Stop();

            _ddpManager.ConfigureSlots(slotPresets);
            _ddpManager.StartStreaming(rate);

            _cts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoop(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                try { _readerTask?.Wait(500); } catch { }
                _cts.Dispose();
                _cts = null;
            }

            _ddpManager.StopStreaming();
        }

        private void ReadLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                byte[]? rawFrame = _transport.ReadMessage(timeoutMs: 50);
                if (rawFrame == null || rawFrame.Length < 11)
                    continue;

                if (!_ddpManager.TryParseStreamFrame(rawFrame, out var parsedPids))
                    continue;

                foreach (var kvp in parsedPids)
                {
                    _latestFrame[kvp.Key] = kvp.Value;
                }

                // Derived GM P59 math channels
                if (_latestFrame.TryGetValue("RPM", out double rpm) && rpm > 0)
                {
                    if (_latestFrame.TryGetValue("InjPW_B1", out double ipw))
                    {
                        _latestFrame["IDC_Pct"] = (ipw * rpm) / 1200.0;
                    }
                    if (_latestFrame.TryGetValue("MAF_gs", out double maf))
                    {
                        _latestFrame["CalcCylAir"] = (maf * 15.0) / rpm;
                    }
                }

                FrameUpdated?.Invoke(new Dictionary<string, double>(_latestFrame));
            }
        }

        public void Dispose()
        {
            Stop();
            _ddpManager.Dispose();
        }
    }
}
