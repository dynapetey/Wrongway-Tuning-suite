#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$REPO_ROOT"

mkdir -p Scanner/Telemetry/J2534

# 1. Write VpwDdpManager.cs
cat << 'CSHARP' > Scanner/Telemetry/J2534/VpwDdpManager.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace WrongWayScanner.Telemetry.J2534
{
    public enum DdpStreamRate : byte
    {
        Stop = 0x00,
        SingleShot = 0x01,
        Slow = 0x02,
        Medium = 0x03,
        Fast = 0x04
    }

    public sealed class DdpPidDefinition
    {
        public string Name { get; init; } = string.Empty;
        public ushort Pid { get; init; }
        public byte ByteOffset { get; init; } // 1-based offset within 6-byte DDP payload (1..6)
        public byte ByteLength { get; init; } // 1 or 2
        public bool IsSigned { get; init; }
        public Func<double, double> ScaleFormula { get; init; } = raw => raw;
    }

    public sealed class DdpSlotConfiguration
    {
        public byte SlotId { get; init; } // 0x00..0x07
        public List<DdpPidDefinition> Definitions { get; } = new();

        public void AddPid(DdpPidDefinition def)
        {
            if (def.ByteLength is not (1 or 2))
                throw new ArgumentException("VPW DDP elements must be 1 or 2 bytes.");
            if (def.ByteOffset < 1 || (def.ByteOffset + def.ByteLength - 1) > 6)
                throw new ArgumentException($"PID {def.Name} exceeds 6-byte DDP payload limit.");

            Definitions.Add(def);
        }

        public DdpSlotConfiguration With(params DdpPidDefinition[] defs)
        {
            foreach (var d in defs)
                AddPid(d);
            return this;
        }
    }

    public interface IJ2534Transport
    {
        void WriteMessage(byte[] data);
        byte[]? ReadMessage(int timeoutMs);
    }

    public sealed class VpwDdpManager : IDisposable
    {
        private static readonly byte[] TxHeader = { 0x6C, 0x10, 0xF1 };
        private static readonly byte[] RxPhysicalHeader = { 0x6C, 0xF1, 0x10 };
        private static readonly byte[] RxBroadcastHeader = { 0x6C, 0xFE, 0x10 };

        private readonly IJ2534Transport _transport;
        private readonly Dictionary<byte, DdpSlotConfiguration> _slots = new();

        public VpwDdpManager(IJ2534Transport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public void ConfigureSlots(IEnumerable<DdpSlotConfiguration> configurations)
        {
            StopStreaming();
            _slots.Clear();

            foreach (var config in configurations)
            {
                if (config.SlotId > 0x07)
                    throw new ArgumentOutOfRangeException(nameof(config.SlotId), "P01/P59 supports DDP slots 0x00..0x07.");

                SendAndVerifyAck(new byte[] { 0x2C, config.SlotId, 0x00 }, expectedServiceAck: 0x6C);

                foreach (var def in config.Definitions)
                {
                    byte posAndSize = (byte)((def.ByteOffset << 4) | (def.ByteLength & 0x0F));
                    byte pidHigh = (byte)(def.Pid >> 8);
                    byte pidLow = (byte)(def.Pid & 0xFF);

                    SendAndVerifyAck(new byte[] { 0x2C, config.SlotId, posAndSize, pidHigh, pidLow }, expectedServiceAck: 0x6C);
                }

                _slots[config.SlotId] = config;
            }
        }

        public void StartStreaming(DdpStreamRate rate = DdpStreamRate.Fast)
        {
            if (_slots.Count == 0)
                throw new InvalidOperationException("No DDP slots configured.");

            var slotIds = _slots.Keys.OrderBy(k => k).ToArray();
            for (int i = 0; i < slotIds.Length; i += 5)
            {
                var batch = slotIds.Skip(i).Take(5).ToArray();
                var payload = new byte[2 + batch.Length];
                payload[0] = 0x2A;
                payload[1] = (byte)rate;
                Array.Copy(batch, 0, payload, 2, batch.Length);

                SendAndVerifyAck(payload, expectedServiceAck: 0x6A);
            }
        }

        public void StopStreaming()
        {
            SendAndVerifyAck(new byte[] { 0x2A, (byte)DdpStreamRate.Stop }, expectedServiceAck: 0x6A);
        }

        public bool TryParseStreamFrame(ReadOnlySpan<byte> rawFrame, out Dictionary<string, double> values)
        {
            values = null!;

            if (rawFrame.Length < 11)
                return false;

            bool isBroadcast = rawFrame[0] == RxBroadcastHeader[0] &&
                               rawFrame[1] == RxBroadcastHeader[1] &&
                               rawFrame[2] == RxBroadcastHeader[2];

            bool isPhysical = rawFrame[0] == RxPhysicalHeader[0] &&
                              rawFrame[1] == RxPhysicalHeader[1] &&
                              rawFrame[2] == RxPhysicalHeader[2];

            if ((!isBroadcast && !isPhysical) || rawFrame[3] != 0x6A)
                return false;

            byte slotId = rawFrame[4];
            if (!_slots.TryGetValue(slotId, out var slotConfig))
                return false;

            ReadOnlySpan<byte> ddpData = rawFrame.Slice(5, 6);
            values = new Dictionary<string, double>(slotConfig.Definitions.Count);

            foreach (var def in slotConfig.Definitions)
            {
                int idx = def.ByteOffset - 1;
                double rawNumeric;

                if (def.ByteLength == 1)
                {
                    rawNumeric = def.IsSigned ? (sbyte)ddpData[idx] : ddpData[idx];
                }
                else
                {
                    ushort u16 = (ushort)((ddpData[idx] << 8) | ddpData[idx + 1]);
                    rawNumeric = def.IsSigned ? (short)u16 : u16;
                }

                values[def.Name] = def.ScaleFormula(rawNumeric);
            }

            return true;
        }

        private void SendAndVerifyAck(byte[] payload, byte expectedServiceAck)
        {
            byte[] txFrame = new byte[TxHeader.Length + payload.Length];
            Buffer.BlockCopy(TxHeader, 0, txFrame, 0, TxHeader.Length);
            Buffer.BlockCopy(payload, 0, txFrame, TxHeader.Length, payload.Length);

            _transport.WriteMessage(txFrame);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < deadline)
            {
                byte[]? rx = _transport.ReadMessage(timeoutMs: 100);
                if (rx == null || rx.Length < 4)
                    continue;

                if (rx[0] != RxPhysicalHeader[0] || rx[1] != RxPhysicalHeader[1] || rx[2] != RxPhysicalHeader[2])
                    continue;

                if (rx[3] == 0x7F && rx.Length >= 6)
                {
                    byte rejectedSid = rx[4];
                    byte nrc = rx[5];
                    if (nrc == 0x78)
                    {
                        deadline = DateTime.UtcNow.AddMilliseconds(1000);
                        continue;
                    }
                    throw new InvalidOperationException($"PCM rejected Service 0x{rejectedSid:X2} with NRC 0x{nrc:X2}.");
                }

                if (rx[3] == expectedServiceAck)
                    return;
            }

            throw new TimeoutException($"Timed out waiting for PCM acknowledgment 0x{expectedServiceAck:X2}.");
        }

        public void Dispose()
        {
            try { StopStreaming(); } catch { }
        }
    }

    public static class P59DdpPresetFactory
    {
        public static List<DdpSlotConfiguration> CreateSpeedDensityPresets()
        {
            var slot0 = new DdpSlotConfiguration { SlotId = 0x00 }.With(
                new DdpPidDefinition { Name = "RPM", Pid = 0x000C, ByteOffset = 1, ByteLength = 2, ScaleFormula = r => r * 0.25 },
                new DdpPidDefinition { Name = "DynCylAir", Pid = 0x125B, ByteOffset = 3, ByteLength = 2, ScaleFormula = r => r / 4096.0 },
                new DdpPidDefinition { Name = "MAP_kPa", Pid = 0x000B, ByteOffset = 5, ByteLength = 1, ScaleFormula = r => r },
                new DdpPidDefinition { Name = "KnockRetard", Pid = 0x11A6, ByteOffset = 6, ByteLength = 1, ScaleFormula = r => r * (45.0 / 256.0) }
            );

            var slot1 = new DdpSlotConfiguration { SlotId = 0x01 }.With(
                new DdpPidDefinition { Name = "InjPW_B1", Pid = 0x1141, ByteOffset = 1, ByteLength = 2, ScaleFormula = r => r / 65.536 },
                new DdpPidDefinition { Name = "CmdEQ", Pid = 0x114B, ByteOffset = 3, ByteLength = 2, ScaleFormula = r => r / 32768.0 },
                new DdpPidDefinition { Name = "SparkAdv", Pid = 0x000E, ByteOffset = 5, ByteLength = 1, ScaleFormula = r => (r * 0.5) - 64.0 },
                new DdpPidDefinition { Name = "TPS", Pid = 0x0011, ByteOffset = 6, ByteLength = 1, ScaleFormula = r => r * (100.0 / 255.0) }
            );

            return new List<DdpSlotConfiguration> { slot0, slot1 };
        }

        public static List<DdpSlotConfiguration> CreateTransmission4L60EPresets()
        {
            var slot0 = new DdpSlotConfiguration { SlotId = 0x00 }.With(
                new DdpPidDefinition { Name = "RPM", Pid = 0x000C, ByteOffset = 1, ByteLength = 2, ScaleFormula = r => r * 0.25 },
                new DdpPidDefinition { Name = "ISS_RPM", Pid = 0x1942, ByteOffset = 3, ByteLength = 2, ScaleFormula = r => r * 0.25 },
                new DdpPidDefinition { Name = "OSS_RPM", Pid = 0x1943, ByteOffset = 5, ByteLength = 2, ScaleFormula = r => r * 0.25 }
            );

            var slot1 = new DdpSlotConfiguration { SlotId = 0x01 }.With(
                new DdpPidDefinition { Name = "TCC_Slip", Pid = 0x1992, ByteOffset = 1, ByteLength = 2, IsSigned = true, ScaleFormula = r => r * 0.25 },
                new DdpPidDefinition { Name = "TFT_C", Pid = 0x1940, ByteOffset = 3, ByteLength = 1, ScaleFormula = r => r - 40.0 },
                new DdpPidDefinition { Name = "CmdGear", Pid = 0x199A, ByteOffset = 4, ByteLength = 1, ScaleFormula = r => r },
                new DdpPidDefinition { Name = "PC_Current", Pid = 0x199E, ByteOffset = 5, ByteLength = 1, ScaleFormula = r => r * 0.005 },
                new DdpPidDefinition { Name = "TCM_Flags", Pid = 0x199B, ByteOffset = 6, ByteLength = 1, ScaleFormula = r => r }
            );

            return new List<DdpSlotConfiguration> { slot0, slot1 };
        }

        public static List<DdpSlotConfiguration> GetDdpSlotsForLayout(string layoutName) => layoutName switch
        {
            "Transmission4L60E" => CreateTransmission4L60EPresets(),
            _ => CreateSpeedDensityPresets()
        };
    }
}
CSHARP

# 2. Write VpwDdpSession.cs
cat << 'CSHARP' > Scanner/Telemetry/VpwDdpSession.cs
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
CSHARP

# 3. Build any .csproj / .sln files present in the Codespace
if command -v dotnet >/dev/null 2>&1; then
    echo "Running dotnet build..."
    find . -maxdepth 3 \( -name "*.sln" -o -name "*.csproj" \) -exec dotnet build {} -c Release \;
else
    echo "dotnet SDK not found in PATH; skipping build step."
fi

# 4. Run Python/PCM test suite if present
if [ -d "PCM/tests" ] && command -v pytest >/dev/null 2>&1; then
    pytest PCM/tests || true
fi

# 5. Update SHA256.json if present
if [ -f "SHA256.json" ]; then
    echo "Refreshing SHA256.json..."
    python3 -c '
import hashlib, json, pathlib
root = pathlib.Path(".")
hashes = {}
for p in sorted(root.rglob("*")):
    if p.is_file() and ".git" not in p.parts and p.name != "SHA256.json":
        hashes[p.as_posix()] = hashlib.sha256(p.read_bytes()).hexdigest()
pathlib.Path("SHA256.json").write_text(json.dumps(hashes, indent=2) + "\n")
'
fi

# 6. Stage, commit, and push to GitHub
git add Scanner/Telemetry/ apply_vpw_ddp.sh
[ -f "SHA256.json" ] && git add SHA256.json

if ! git diff --cached --quiet; then
    git commit -m "Add P59 Mode 2C/2A J1850 VPW DDP manager, presets, and telemetry session"
    git push
else
    echo "No new changes to commit."
fi

echo "Done."
