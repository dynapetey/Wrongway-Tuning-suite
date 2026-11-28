using System;
using System.Runtime.InteropServices;

namespace WrongWayScanner.Telemetry.J2534
{
    public sealed class J2534ObdxTransport : IJ2534Transport
    {
        private const uint J1850VPW = 0x01;
        private const uint PassFilter = 0x01;
        private const uint StatusNoError = 0x00;

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

        private delegate int PassThruOpenDelegate(IntPtr pName, out uint pDeviceID);
        private delegate int PassThruCloseDelegate(uint deviceID);
        private delegate int PassThruConnectDelegate(uint deviceID, uint protocolID, uint flags, uint baudRate, out uint pChannelID);
        private delegate int PassThruDisconnectDelegate(uint channelID);
        private delegate int PassThruReadMsgsDelegate(uint channelID, IntPtr pMsg, ref uint pNumMsgs, uint timeout);
        private delegate int PassThruWriteMsgsDelegate(uint channelID, IntPtr pMsg, ref uint pNumMsgs, uint timeout);
        private delegate int PassThruStartMsgFilterDelegate(uint channelID, uint filterType, IntPtr pMaskMsg, IntPtr pPatternMsg, IntPtr pFlowControlMsg, out uint pFilterID);

        private readonly IntPtr _libHandle;
        private readonly uint _deviceId;
        private readonly uint _channelId;
        private readonly PassThruCloseDelegate _close;
        private readonly PassThruDisconnectDelegate _disconnect;
        private readonly PassThruReadMsgsDelegate _readMsgs;
        private readonly PassThruWriteMsgsDelegate _writeMsgs;

        public J2534ObdxTransport(string dllPath)
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
            CheckStatus(connect(_deviceId, J1850VPW, 0, 10400, out _channelId), "PassThruConnect");

            // Allow both 6C F1 10 (Physical Ack) and 6C FE 10 (Mode 2A DDP Broadcast)
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
            int size = Marshal.SizeOf<PassThruMsg>();
            IntPtr msgPtr = Marshal.AllocHGlobal(size);
            try
            {
                var empty = new PassThruMsg { ProtocolID = J1850VPW, Data = new byte[4128] };
                Marshal.StructureToPtr(empty, msgPtr, false);

                uint numMsgs = 1;
                int status = _readMsgs(_channelId, msgPtr, ref numMsgs, (uint)Math.Max(0, timeoutMs));
                if (status != StatusNoError || numMsgs == 0)
                    return null;

                var rx = Marshal.PtrToStructure<PassThruMsg>(msgPtr);
                if (rx.DataSize == 0 || rx.Data == null)
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
                ProtocolID = J1850VPW,
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
}
