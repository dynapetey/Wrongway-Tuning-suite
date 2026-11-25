// SPDX-License-Identifier: GPL-3.0-only
namespace WrongWay;

sealed partial class HammerdroidProbe
{
    public static Task<string[]> ReadCodes(string port, Action<string> status, CancellationToken token) =>
        WithAdapter(port, status, token, async (client, _) =>
        {
            await client.EnableVpw(0x6B);
            status("Reading stored trouble codes from the PCM…");
            return await client.ReadStoredCodes();
        });

    private async Task<string[]> ReadStoredCodes()
    {
        // PID 01 reports the number of confirmed emissions DTCs in its first data byte.
        var monitor = await ReadSaePid(0x01);
        int? expected = monitor is null ? null : monitor[5] & 0x7F;
        if (expected == 0)
            return [];

        await serial.Send(Encode(0x10, [0x68, 0x6A, 0xF1, 0x03]));
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        bool answered = false;
        long until = Environment.TickCount64 + 1800;
        while (true)
        {
            (byte command, byte[] frame) packet;
            try { packet = await Packet(until); }
            catch (TimeoutException) { break; }

            var (command, frame) = packet;
            if (command is not (0x08 or 0x09) || frame.Length < 6 ||
                frame[0] is not (0x48 or 0x68) || frame[1] != 0x6B || frame[2] != 0x10)
                continue;
            if (frame[3] == 0x7F && frame[4] == 0x03)
            {
                if (frame[5] == 0x78) // Response pending
                    continue;
                throw new IOException($"The PCM rejected the stored-code request (status {frame[5]:X2}).");
            }
            if (frame[3] != 0x43 || frame.Length > 11)
                continue;

            // Each legacy J1850 reply carries up to three two-byte DTCs plus an optional bus CRC.
            int end = frame.Length;
            if ((end & 1) != 0)
            {
                if (!HasVpwCrc(frame))
                    throw new IOException("A stored-code reply failed its VPW checksum. Retry the read.");
                end--;
            }
            answered = true;
            for (int i = 4; i < end; i += 2)
            {
                int high = frame[i], low = frame[i + 1];
                if (high != 0 || low != 0)
                    codes.Add($"{"PCBU"[high >> 6]}{(high >> 4) & 3}{high & 15:X}{low:X2}");
            }
            if (expected == codes.Count)
                break;
        }

        token.ThrowIfCancellationRequested();
        if (!answered)
            throw new IOException("No stored-code reply received from the PCM. Check key-on power and retry.");
        if (expected is int count && codes.Count != count)
            throw new IOException($"The PCM reported {count} stored codes, but returned {codes.Count}. Retry the read.");
        return codes.ToArray();
    }
}
