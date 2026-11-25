using System.Runtime.InteropServices;

namespace WrongWay;

// Linux native J2534 transport for SAE PID discovery. The C library is packaged beside Scanner.
internal static class J2534Gt
{
    private const uint J1850Vpw = 1;
    private const int Ok = 0;
    [StructLayout(LayoutKind.Sequential)]
    private struct PassThruMessage
    {
        public uint ProtocolID, RxStatus, TxFlags, Timestamp, DataSize, ExtraDataIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
        public byte[] Data;
        public static PassThruMessage Create() => new() { ProtocolID = J1850Vpw, Data = new byte[4128] };
    }
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruOpen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out uint device);
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruClose", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(uint device);
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruConnect", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Connect(uint device, uint protocol, uint flags, uint baud, out uint channel);
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruDisconnect", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Disconnect(uint channel);
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruWriteMsgs", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Write(uint channel, ref PassThruMessage message, ref uint count, uint timeout);
    [DllImport("libobdx_j2534.so", EntryPoint = "PassThruReadMsgs", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Read(uint channel, ref PassThruMessage message, ref uint count, uint timeout);

    public static Task<GtPidDiscovery> Discover(string path, Action<string> status, CancellationToken token) =>
        Task.Run(() => DiscoverCore(path, status, token), token);

    private static GtPidDiscovery DiscoverCore(string path, Action<string> status, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libobdx_j2534.so")))
            throw new IOException("Linux J2534 driver library is missing beside WrongWayScanner.");
        int result = Open(path, out uint device);
        if (result != Ok)
            throw new IOException($"J2534 could not open GT on {path}, error {result}.");
        uint channel = 0;
        try
        {
            result = Connect(device, J1850Vpw, 0, 10400, out channel);
            if (result != Ok)
                throw new IOException($"J2534 J1850 VPW connection failed, error {result}.");
            status("J2534 connected. Reading SAE supported PID bitmaps...");
            var found = new SortedSet<uint>();
            bool complete = false;
            var warning = (string?)null;
            for (int group = 0; group <= 0xE0; group += 0x20)
            {
                token.ThrowIfCancellationRequested();
                var request = PassThruMessage.Create();
                byte[] payload = [0, 0x68, 0x6A, 0xF0, 0x01, (byte)group];
                Array.Copy(payload, request.Data, payload.Length);
                request.DataSize = (uint)payload.Length;
                uint count = 1;
                result = Write(channel, ref request, ref count, 500);
                if (result != Ok || count != 1)
                    throw new IOException($"J2534 failed to send PID page {group:X2}, error {result}.");
                byte[]? page = null;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var response = PassThruMessage.Create();
                    count = 1;
                    result = Read(channel, ref response, ref count, 200);
                    if (result != Ok || count != 1)
                        continue;
                    // Native VPW backend prepends a 00 byte to the on-wire GM 3-byte header.
                    if (response.DataSize >= 10 && response.Data[0] == 0 &&
                        response.Data[4] == 0x41 && response.Data[5] == (byte)group)
                    {
                        page = response.Data[6..10];
                        break;
                    }
                }
                if (page is null)
                {
                    warning = $"No SAE PID support response for page 0x{group:X2}.";
                    break;
                }
                uint bitmap = ((uint)page[0] << 24) | ((uint)page[1] << 16) |
                              ((uint)page[2] << 8) | page[3];
                for (int index = 0; index < 31; index++)
                    if ((bitmap & (0x80000000u >> index)) != 0)
                        found.Add((uint)(group + index + 1));
                if ((bitmap & 1) == 0 || group == 0xE0)
                {
                    complete = true;
                    break;
                }
            }
            if (found.Count == 0 && !complete)
                throw new IOException(warning ?? "The PCM returned no SAE PID support bitmap.");
            status($"J2534 SAE discovery: {found.Count} supported PIDs.");
            return new GtPidDiscovery(null, found.ToArray(), complete, [], 0,
                warning ?? "J2534 discovery currently reads SAE PIDs only; GM enhanced scanning requires a separate implementation.");
        }
        finally
        {
            if (channel != 0) Disconnect(channel);
            Close(device);
        }
    }

    public static Task Record(string port, string profile, string path,
        Action<string> status, Action<LiveReading> reading, CancellationToken token) =>
        Task.Run(() => RecordCore(port, profile, path, status, reading, token), token);

    private static void RecordCore(string port, string profile, string path,
        Action<string> status, Action<LiveReading> reading, CancellationToken token)
    {
        int result = Open(port, out uint device);
        if (result != Ok) throw new IOException($"J2534 failed opening GT ({result}).");
        uint channel = 0;
        try
        {
            result = Connect(device, J1850Vpw, 0, 10400, out channel);
            if (result != Ok) throw new IOException($"J2534 VPW connection failed ({result}).");
            byte[] pids = profile == "FuelTrims" ? [0x06, 0x07, 0x08, 0x09]
                : [0x05, 0x0C, 0x06, 0x07, 0x08, 0x09];
            LiveColumn[] columns = pids.Select(pid => pid switch
            {
                0x05 => new LiveColumn("SAE05", "Coolant temperature", "°C", pid),
                0x0C => new LiveColumn("SAE0C", "Engine speed", "rpm", pid),
                0x06 => new LiveColumn("SAE06", "STFT Bank 1", "%", pid),
                0x07 => new LiveColumn("SAE07", "LTFT Bank 1", "%", pid),
                0x08 => new LiveColumn("SAE08", "STFT Bank 2", "%", pid),
                _ => new LiveColumn("SAE09", "LTFT Bank 2", "%", pid)
            }).ToArray();
            using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
            using var diagnostics = new StreamWriter(new FileStream(path + ".diagnostics.log", FileMode.CreateNew, FileAccess.Write)) { AutoFlush = true };
            writer.WriteLine("Time (s)," + string.Join(",", columns.Select(c => $"\"{c.Name} [{c.Unit}]\"")));
            writer.Flush();
            status("J2534 SAE-only recording started (GM enhanced channels unavailable).");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int missed = 0;
            while (!token.IsCancellationRequested)
            {
                var values = new List<double?>();
                foreach (byte pid in pids)
                {
                    token.ThrowIfCancellationRequested();
                    byte[]? raw = RequestPid(channel, pid, diagnostics, token);
                    double? value = raw is null ? null : pid switch
                    {
                        0x05 when raw.Length >= 1 => raw[0] - 40,
                        0x0C when raw.Length >= 2 => (raw[0] * 256 + raw[1]) / 4d,
                        >= 0x06 and <= 0x09 when raw.Length >= 1 => (raw[0] - 128) * 100d / 128d,
                        _ => null
                    };
                    values.Add(value);
                }
                long elapsed = stopwatch.ElapsedMilliseconds;
                if (values.All(n => n is null))
                {
                    if (++missed >= 5) throw new IOException("No SAE responses after five cycles; see diagnostics.");
                }
                else missed = 0;
                writer.WriteLine((elapsed / 1000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + string.Join(",", values.Select(n => n?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "")));
                writer.Flush();
                reading(new LiveReading(elapsed, columns, values.Select(n => new Reading(elapsed, n)).ToArray()));
                // Bound polling rate to avoid flooding the vehicle's J1850 VPW bus.
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (channel != 0) Disconnect(channel);
            Close(device);
        }
    }

    private static byte[]? RequestPid(uint channel, byte pid, TextWriter log, CancellationToken token)
    {
        var message = PassThruMessage.Create();
        byte[] request = [0, 0x68, 0x6A, 0xF0, 0x01, pid];
        Array.Copy(request, message.Data, request.Length);
        message.DataSize = (uint)request.Length;
        uint count = 1;
        int status = Write(channel, ref message, ref count, 500);
        if (status != Ok || count != 1)
        {
            log.WriteLine($"{DateTimeOffset.UtcNow:O} TX PID {pid:X2} failed ({status})");
            return null;
        }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var response = PassThruMessage.Create();
            count = 1;
            status = Read(channel, ref response, ref count, 150);
            if (status != Ok || count != 1) continue;
            int len = (int)Math.Min(response.DataSize, 48);
            log.WriteLine($"{DateTimeOffset.UtcNow:O} RX {Convert.ToHexString(response.Data.AsSpan(0, len))}");
            if (response.DataSize >= 7 && response.Data[0] == 0 &&
                response.Data[4] == 0x41 && response.Data[5] == pid)
                return response.Data[6..(int)response.DataSize];
        }
        log.WriteLine($"{DateTimeOffset.UtcNow:O} TIMEOUT PID {pid:X2}");
        return null;
    }
}
