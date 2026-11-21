using System.Buffers.Binary;

namespace WrongWay;

sealed record GtPidDiscovery(uint? OsId, uint[] SaePids, bool SaeComplete, uint[] GmPids, int GmProbed, string? Warning)
{
    public bool Supports(uint pid) => Array.BinarySearch(SaePids, pid) >= 0 || Array.BinarySearch(GmPids, pid) >= 0;
    public int Count => SaePids.Length + GmPids.Length;
}

sealed partial class HammerdroidProbe
{
    public static Task<GtPidDiscovery> DiscoverPids(string device, Action<string> status, CancellationToken token) =>
        WithAdapter(device, status, token, (client, _) => client.ReadAvailablePids(status));

    private async Task<GtPidDiscovery> ReadAvailablePids(Action<string> status)
    {
        await EnableVpw(0x6B);
        var warnings = new List<string>();
        var standard = new List<uint>();
        bool complete = false;

        status("Reading SAE PID support from the ECM…");
        for (int group = 0; group <= 0xE0; group += 0x20)
        {
            byte[]? reply = await ReadSaePid((byte)group);
            if (reply is null && group == 0)
                reply = await ReadSaePid(0);
            if (reply is null)
            {
                warnings.Add(group == 0 ? "The ECM did not return an SAE support bitmap." :
                    $"The ECM did not finish its SAE support list at page 0x{group:X2}.");
                break;
            }

            uint bits = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(5, 4));
            for (int i = 0; i < 31; i++)
                if ((bits & (0x80000000u >> i)) != 0)
                    standard.Add((uint)(group + i + 1));
            if ((bits & 1) == 0 || group == 0xE0)
            {
                complete = true;
                break;
            }
        }

        await SetReplyAddress(0xF0);
        uint? os = null;
        try
        {
            status("Identifying the ECM operating system for its GM PID definitions…");
            uint candidate = await ReadPcmOsId(status);
            if (candidate is 0 or uint.MaxValue)
                warnings.Add("The ECM returned an invalid operating system ID.");
            else
                os = candidate;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            warnings.Add(ex.Message);
        }

        var enhanced = new List<uint>();
        int probed = 0;
        if (os is uint osId)
        {
            uint[] candidates = LiveScanner.Catalog(osId)
                .Where(channel => channel.Pid is > 0xFF and <= 0xFFFF)
                .Select(channel => channel.Pid!.Value).Distinct().Order().ToArray();
            foreach (uint pid in candidates)
            {
                if (probed % 8 == 0)
                    status($"Checking GM PIDs on the ECM · {probed}/{candidates.Length} tested");
                byte high = (byte)(pid >> 8), low = (byte)pid;
                byte[]? reply = await ReadPidReply([0x6C, 0x10, 0xF0, 0x22, high, low, 0x01],
                    frame => frame[0] == 0x6C && frame[3] == 0x62 && frame.Length >= 7 && frame[4] == high && frame[5] == low, 300);
                probed++;
                if (reply is not null)
                    enhanced.Add(pid);
            }
        }
        else
            warnings.Add("GM enhanced PID checks require an identified ECM OS.");

        token.ThrowIfCancellationRequested();
        if (!complete && standard.Count == 0 && enhanced.Count == 0)
            throw new IOException("No supported PID response received from the ECM. Check key-on power and the VPW connection.");
        return new GtPidDiscovery(os, standard.ToArray(), complete, enhanced.ToArray(), probed,
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private Task<byte[]?> ReadSaePid(byte pid) =>
        ReadPidReply([0x68, 0x6A, 0xF1, 0x01, pid],
            frame => frame[0] is 0x48 or 0x68 && frame[3] == 0x41 && frame.Length is 9 or 10 &&
                     frame[4] == pid && (frame.Length == 9 || HasVpwCrc(frame)), 550);

    private async Task<byte[]?> ReadPidReply(byte[] request, Func<byte[], bool> matches, int milliseconds)
    {
        token.ThrowIfCancellationRequested();
        await serial.Send(Encode(0x10, request));
        long until = Environment.TickCount64 + milliseconds;
        try
        {
            while (true)
            {
                var (command, frame) = await Packet(until);
                if (command is not (0x08 or 0x09) || frame.Length < 5 ||
                    frame[2] != 0x10 || !(frame[1] == 0xF0 || request[3] == 0x01 && frame[1] == 0x6B))
                    continue;
                if (frame[3] == 0x7F && frame[4] == request[3])
                {
                    if (frame.Length > 5 && frame[5] == 0x78)
                        continue;
                    return null;
                }
                if (matches(frame))
                    return frame;
            }
        }
        catch (TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }
}
