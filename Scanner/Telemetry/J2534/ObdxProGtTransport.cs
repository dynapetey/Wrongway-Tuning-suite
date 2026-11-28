using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace WrongWayScanner.Telemetry.J2534
{
    public static class ObdxTransportFactory
    {
        public static IJ2534Transport CreateAuto(string? explicitPortOrDll = null)
        {
            if (OperatingSystem.IsWindows())
            {
                string dllPath = explicitPortOrDll ?? FindObdxJ2534DllPath();
                return new WindowsJ2534ObdxTransport(dllPath);
            }

            if (OperatingSystem.IsLinux())
            {
                string serialPort = explicitPortOrDll ?? FindLinuxObdxSerialPort();
                return new LinuxDviObdxTransport(serialPort);
            }

            throw new PlatformNotSupportedException("OBDX Pro GT transport supports Windows J2534 and Linux Serial DVI.");
        }

        private static string FindObdxJ2534DllPath()
        {
            if (OperatingSystem.IsWindows())
            {
                string[] regRoots =
                {
                    @"SOFTWARE\PassThruSupport.04.04",
                    @"SOFTWARE\WOW6432Node\PassThruSupport.04.04"
                };

                foreach (string root in regRoots)
                {
                    using RegistryKey? baseKey = Registry.LocalMachine.OpenSubKey(root);
                    if (baseKey == null) continue;

                    foreach (string subName in baseKey.GetSubKeyNames())
                    {
                        if (!subName.Contains("OBDX", StringComparison.OrdinalIgnoreCase))
                            continue;

                        using RegistryKey? devKey = baseKey.OpenSubKey(subName);
                        string? fnLib = devKey?.GetValue("FunctionLibrary") as string;
                        if (!string.IsNullOrWhiteSpace(fnLib))
                            return fnLib;
                    }
                }
            }

            return "obdx_j2534.dll";
        }

        private static string FindLinuxObdxSerialPort()
        {
            foreach (string port in SerialPort.GetPortNames())
            {
                if (port.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase) ||
                    port.Contains("ttyACM", StringComparison.OrdinalIgnoreCase))
                {
                    return port;
                }
            }
            return "/dev/ttyUSB0";
        }
    }

    public sealed class WindowsJ2534ObdxTransport : IJ2534Transport
    {
        private const uint ProtocolJ1850Vpw = 0x01;
        private const uint BaudRateVpw = 10400;
        private const uint PassFilter = 0x01;
        private const uint StatusNoError = 0x00;
        private const uint ErrBufferEmpty = 0x10;
        private const uint ErrTimeout = 0x09;

        [StructLayout(LayoutKind.Sequential)]
        private struct PassThruMsg
        {
            public uint ProtocolID;
            public uint RxStatus;
            public uint TxFlags;
            public uint Timestamp;
            public uint DataSize;
            public uint ExtraDataIndex;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
            public byte[] Data;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruOpenDelegate(IntPtr pName, out uint pDeviceID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruCloseDelegate(uint deviceID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruConnectDelegate(uint deviceID, uint protocolID, uint flags, uint baudRate, out uint pChannelID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruDisconnectDelegate(uint channelID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruReadMsgsDelegate(uint channelID, IntPtr pMsg, ref uint pNumMsgs, uint timeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruWriteMsgsDelegate(uint channelID, IntPtr pMsg, ref uint pNumMsgs, uint timeout);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PassThruStartMsgFilterDelegate(
            uint channelID, uint filterType, IntPtr pMaskMsg, IntPtr pPatternMsg, IntPtr pFlowControlMsg, out uint pFilterID);

        private readonly IntPtr _libHandle;
        private readonly uint _deviceId;
        private readonly uint _channelId;
        private readonly PassThruCloseDelegate _close;
        private readonly PassThruDisconnectDelegate _disconnect;
        private readonly PassThruReadMsgsDelegate _readMsgs;
        private readonly PassThruWriteMsgsDelegate _writeMsgs;

        public WindowsJ2534ObdxTransport(string dllPath)
        {
            _libHandle = NativeLibrary.Load(dllPath);

            var open = GetProc<PassThruOpenDelegate>("PassThruOpen");
            _close = GetProc<PassThruCloseDelegate>("PassThruClose");
            var connect = GetProc<PassThruConnectDelegate>("PassThruConnect");
            _disconnect = GetProc<PassThruDisconnectDelegate>("PassThruDisconnect");
            _readMsgs = GetProc<PassThruReadMsgsDelegate>("PassThruReadMsgs");
            _writeMsgs = GetProc<PassThruWriteMsgsDelegate>("PassThruWriteMsgs");
            var startFilter = GetProc<PassThruStartMsgFilterDelegate>("PassThruStartMsgFilter");

            CheckStatus(open(IntPtr.Zero, out _deviceId), "PassThruOpen");
            CheckStatus(connect(_deviceId, ProtocolJ1850Vpw, 0, BaudRateVpw, out _channelId), "PassThruConnect");

            InstallPassFilter(startFilter, new byte[] { 0xFF, 0xFF, 0xFF, 0x00 }, new byte[] { 0x6C, 0xF1, 0x10, 0x00 });
            InstallPassFilter(startFilter, new byte[] { 0xFF, 0xFF, 0xFF, 0x00 }, new byte[] { 0x6C, 0xFE, 0x10, 0x00 });
        }

        private T GetProc<T>(string name) where T : Delegate
        {
            IntPtr ptr = NativeLibrary.GetExport(_libHandle, name);
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private void InstallPassFilter(PassThruStartMsgFilterDelegate startFilter, byte[] mask, byte[] pattern)
        {
            IntPtr maskPtr = AllocMsg(mask);
            IntPtr patternPtr = AllocMsg(pattern);
            try
            {
                CheckStatus(startFilter(_channelId, PassFilter, maskPtr, patternPtr, IntPtr.Zero, out _), "PassThruStartMsgFilter");
            }
            finally
            {
                Marshal.FreeHGlobal(maskPtr);
                Marshal.FreeHGlobal(patternPtr);
            }
        }

        public void WriteMessage(byte[] data)
        {
            IntPtr msgPtr = AllocMsg(data);
            try
            {
                uint numMsgs = 1;
                CheckStatus(_writeMsgs(_channelId, msgPtr, ref numMsgs, 250), "PassThruWriteMsgs");
            }
            finally
            {
                Marshal.FreeHGlobal(msgPtr);
            }
        }

        public byte[]? ReadMessage(int timeoutMs)
        {
            int structSize = Marshal.SizeOf<PassThruMsg>();
            IntPtr msgPtr = Marshal.AllocHGlobal(structSize);
            try
            {
                var empty = new PassThruMsg { ProtocolID = ProtocolJ1850Vpw, Data = new byte[4128] };
                Marshal.StructureToPtr(empty, msgPtr, false);

                uint numMsgs = 1;
                uint status = (uint)_readMsgs(_channelId, msgPtr, ref numMsgs, (uint)Math.Max(0, timeoutMs));
                if (status == ErrBufferEmpty || status == ErrTimeout || numMsgs == 0)
                    return null;

                if (status != StatusNoError)
                    throw new InvalidOperationException($"J2534 PassThruReadMsgs failed: 0x{status:X2}");

                var rx = Marshal.PtrToStructure<PassThruMsg>(msgPtr);
                if (rx.DataSize == 0 || rx.Data == null)
                    return null;

                if ((rx.RxStatus & 0x01) != 0)
                    return null;

                byte[] result = new byte[rx.DataSize];
                Array.Copy(rx.Data, result, (int)rx.DataSize);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(msgPtr);
            }
        }

        private static IntPtr AllocMsg(byte[] payload)
        {
            var msg = new PassThruMsg
            {
                ProtocolID = ProtocolJ1850Vpw,
                DataSize = (uint)payload.Length,
                Data = new byte[4128]
            };
            Array.Copy(payload, msg.Data, payload.Length);

            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PassThruMsg>());
            Marshal.StructureToPtr(msg, ptr, false);
            return ptr;
        }

        private static void CheckStatus(int status, string fn)
        {
            if (status != StatusNoError)
                throw new InvalidOperationException($"J2534 {fn} failed with error code 0x{status:X2}.");
        }

        public void Dispose()
        {
            try { _disconnect(_channelId); } catch { }
            try { _close(_deviceId); } catch { }
            if (_libHandle != IntPtr.Zero)
                NativeLibrary.Free(_libHandle);
        }
    }

    public sealed class LinuxDviObdxTransport : IJ2534Transport
    {
        private const byte DviSyncByte = 0xAA;
        private const byte CmdSetProtocol = 0x01;
        private const byte CmdTxVpwFrame = 0x10;
        private const byte CmdRxVpwFrame = 0x11;

        private readonly SerialPort _port;
        private readonly BlockingCollection<byte[]> _rxQueue = new(boundedCapacity: 256);
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _rxThread;

        public LinuxDviObdxTransport(string portName = "/dev/ttyUSB0", int baudRate = 115200)
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                WriteTimeout = 250,
                DtrEnable = true,
                RtsEnable = true
            };
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();

            _rxThread = new Thread(ReadSerialLoop)
            {
                IsBackground = true,
                Name = "OBDX-Linux-DVI-Reader"
            };
            _rxThread.Start();

            SendDviCommand(CmdSetProtocol, new byte[] { 0x01 });
        }

        public void WriteMessage(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("VPW payload cannot be empty.", nameof(data));

            SendDviCommand(CmdTxVpwFrame, data);
        }

        public byte[]? ReadMessage(int timeoutMs)
        {
            return _rxQueue.TryTake(out byte[]? frame, Math.Max(0, timeoutMs)) ? frame : null;
        }

        private void SendDviCommand(byte command, byte[] payload)
        {
            byte len = (byte)(1 + payload.Length);
            byte[] packet = new byte[3 + payload.Length + 1];
            packet[0] = DviSyncByte;
            packet[1] = len;
            packet[2] = command;
            Buffer.BlockCopy(payload, 0, packet, 3, payload.Length);

            byte checksum = 0;
            for (int i = 1; i < packet.Length - 1; i++)
                checksum ^= packet[i];
            packet[^1] = checksum;

            lock (_port)
            {
                _port.Write(packet, 0, packet.Length);
            }
        }

        private void ReadSerialLoop()
        {
            var buffer = new List<byte>(128);
            byte[] chunk = new byte[64];

            while (!_cts.IsCancellationRequested && _port.IsOpen)
            {
                try
                {
                    int read = _port.Read(chunk, 0, chunk.Length);
                    if (read <= 0) continue;

                    for (int i = 0; i < read; i++)
                        buffer.Add(chunk[i]);

                    ExtractDviPackets(buffer);
                }
                catch (TimeoutException) { }
                catch (Exception) when (_cts.IsCancellationRequested) { break; }
            }
        }

        private void ExtractDviPackets(List<byte> buffer)
        {
            while (buffer.Count >= 4)
            {
                int syncIdx = buffer.IndexOf(DviSyncByte);
                if (syncIdx < 0)
                {
                    buffer.Clear();
                    return;
                }
                if (syncIdx > 0)
                    buffer.RemoveRange(0, syncIdx);

                if (buffer.Count < 4)
                    return;

                byte len = buffer[1];
                int totalPacketLen = 2 + len + 1;
                if (buffer.Count < totalPacketLen)
                    return;

                byte expectedXor = 0;
                for (int i = 1; i < totalPacketLen - 1; i++)
                    expectedXor ^= buffer[i];

                byte actualXor = buffer[totalPacketLen - 1];
                if (expectedXor != actualXor)
                {
                    buffer.RemoveAt(0);
                    continue;
                }

                byte cmd = buffer[2];
                int vpwLen = len - 1;
                if (cmd == CmdRxVpwFrame && vpwLen > 0)
                {
                    byte[] vpwFrame = new byte[vpwLen];
                    buffer.CopyTo(3, vpwFrame, 0, vpwLen);
                    _rxQueue.TryAdd(vpwFrame);
                }

                buffer.RemoveRange(0, totalPacketLen);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { if (_port.IsOpen) _port.Close(); } catch { }
            _port.Dispose();
            _cts.Dispose();
            _rxQueue.Dispose();
        }
    }
}
