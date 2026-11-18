using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using PcmHacking;

namespace WrongWay;

sealed record GtAdapter(string Name, string Firmware, string Hardware, string Serial, double Voltage);
sealed record GtPcm(uint OsId, string? Vin);
sealed record GtInspection(GtAdapter Adapter, GtPcm? Pcm, string? PcmError);

/// <summary>Linux transport for the identification operations in Hammerdroid's DVI/VPW client.</summary>
sealed partial class HammerdroidProbe(LinuxSerialPort serial, CancellationToken token)
{
    private readonly byte[] buffer = new byte[256];
    private int buffered;
    private int nextByte;
    private bool inDviMode;

    public static Task<GtInspection> Inspect(
        string device, bool includePcm, Action<string> status, CancellationToken token) =>
        WithAdapter<GtInspection>(device, status, token, async (client, adapter) =>
        {
            if (!includePcm)
                return new GtInspection(adapter, null, null);
            status("Reading PCM operating system ID and VIN over VPW…");
            GtPcm? pcm = null;
            try
            {
                await client.EnableVpw();
                pcm = new GtPcm(await client.ReadPcmOsId(status), null);
                string vin = await client.ReadVin(status);
                return new GtInspection(adapter, pcm with { Vin = vin }, null);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                return new GtInspection(adapter, pcm, ex.Message);
            }
        });

    private static async Task<T> WithAdapter<T>(string device, Action<string> status, CancellationToken token,
        Func<HammerdroidProbe, GtAdapter, Task<T>> operation)
    {
        if (!device.StartsWith("/dev/", StringComparison.Ordinal))
            throw new IOException("Choose a Linux USB serial or RFCOMM device.");
        token.ThrowIfCancellationRequested();
        using var serial = new LinuxSerialPort(device);
        await serial.OpenAsync(new SerialPortConfiguration { BaudRate = 115200, Timeout = 70 });
        var client = new HammerdroidProbe(serial, token);
        try
        {
            status("Starting Hammerdroid’s OBDX Pro GT handshake…");
            GtAdapter adapter = await client.Initialize();
            return await operation(client, adapter);
        }
        finally
        {
            if (client.inDviMode)
            {
                // Return the adapter to ELM mode so the scanner can start its next session.
                try { await serial.Send(Encode(0x25, [])); }
                catch (IOException) { }
                catch (TimeoutException) { }
            }
        }
    }

    private async Task<GtAdapter> Initialize()
    {
        await serial.Send(Encode(0x25, []));
        await Task.Delay(250, token);
        await serial.DiscardBuffers();

        // Some firmware needs time to reboot after ATZ and may miss that prompt.
        try { await Elm("ATZ", 1100); }
        catch (TimeoutException)
        {
            nextByte = buffered = 0;
            await serial.DiscardBuffers();
        }

        string description = await Elm("AT@1", 1100);
        if (!description.Contains("OBDX", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected serial device did not identify itself as OBDX.");
        string response = await Elm("DXDP1", 1100);
        if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The adapter refused DVI mode.");
        inDviMode = true;

        string name = Encoding.ASCII.GetString(await AdapterValue(0x22, 0x03)).Trim('\0', ' ');
        if (!name.Contains("GT", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The adapter reported “{name}”. Select an OBDX Pro GT.");
        string firmware = string.Join(".", await AdapterValue(0x22, 0x01));
        string hardware = string.Join(".", await AdapterValue(0x22, 0x00));
        string serialNumber = Convert.ToHexString(await AdapterValue(0x22, 0x04));
        byte[] voltage = await Command(0x3A, [0x00, 0x00]); // Read ADC channel 0.
        if (voltage.Length != 4 || voltage[0] != 0x00 || voltage[1] != 0x00)
            throw new IOException("The GT returned an invalid voltage response for ADC channel 0.");
        double measured = BinaryPrimitives.ReadUInt16BigEndian(voltage.AsSpan(2)) * 0.009047468 + 0.2;
        return new GtAdapter(name, firmware, hardware, serialNumber, measured);
    }

    private async Task EnableVpw()
    {
        // Select VPW, set tester address F0, and enable network receive.
        await Command(0x31, [0x01, 0x01]);
        await Command(0x33, [0x00, 0xF0, 0x01]);
        await Command(0x31, [0x02, 0x01]);
    }

    private async Task<uint> ReadPcmOsId(Action<string> status)
    {
        byte[] os = await ReadPcmBlock(0x0A, status);
        if (os.Length < 4)
            throw new IOException("The PCM returned an incomplete OS ID.");
        return BinaryPrimitives.ReadUInt32BigEndian(os);
    }

    private async Task<string> ReadVin(Action<string> status)
    {
        var vin = new byte[17];
        int offset = 0;
        for (byte part = 1; part <= 3; part++)
        {
            byte[] bytes = await ReadPcmBlock(part, status);
            bytes.CopyTo(vin, offset);
            offset += bytes.Length;
        }
        string identifier = Encoding.ASCII.GetString(vin);
        if (!Regex.IsMatch(identifier, @"\A[A-HJ-NPR-Z0-9]{17}\z"))
            throw new IOException("The PCM returned an invalid VIN.");
        return identifier;
    }

    private async Task<byte[]> ReadPcmBlock(byte block, Action<string> status)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await RequestPcmBlock(block); }
            catch (TimeoutException) when (attempt < 3)
            {
                status($"PCM block {block:X2} did not answer. Retrying {attempt + 1}/3…");
                await Task.Delay(150 * attempt, token);
            }
        }
    }

    private async Task<byte[]> RequestPcmBlock(byte block)
    {
        await serial.Send(Encode(0x10, [0x6C, 0x10, 0xF0, 0x3C, block]));
        long until = Environment.TickCount64 + 2500;
        while (true)
        {
            var (command, payload) = await Packet(until);
            if (command is not (0x08 or 0x09) || payload.Length < 5 ||
                payload[0] != 0x6C || payload[1] != 0xF0 || payload[2] != 0x10)
                continue;
            if (payload[3] == 0x7F && payload[4] == 0x3C)
                throw new IOException(payload.Length > 5
                    ? $"The PCM rejected identification (VPW status {payload[5]:X2})."
                    : "The PCM rejected identification.");
            if (payload[3] == 0x7C && payload[4] == block)
                return block is >= 1 and <= 3 ? ReadVinBlock(payload, block) : payload[5..];
        }
    }

    private static byte[] ReadVinBlock(byte[] frame, byte block)
    {
        // GM sends 5/6/6 VIN characters, with an optional leading zero on the first block.
        int start = block == 1 && frame.Length > 5 && frame[5] == 0 ? 6 : 5;
        int end = start + (block == 1 ? 5 : 6);
        // Some DVI configurations forward the J1850 CRC in addition to the DVI checksum.
        if (frame.Length == end + 1 && !HasVpwCrc(frame))
            throw new IOException($"PCM VIN block {block:X2} has a bad VPW CRC.");
        if (frame.Length != end && frame.Length != end + 1)
            throw new IOException($"PCM VIN block {block:X2} has an unexpected length.");
        return frame[start..end];
    }

    private static bool HasVpwCrc(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0xFF;
        foreach (byte value in bytes[..^1])
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (byte)((crc << 1) ^ ((crc & 0x80) != 0 ? 0x1D : 0));
        }
        return (crc ^ 0xFF) == bytes[^1];
    }

    private async Task<string> Elm(string command, int milliseconds)
    {
        token.ThrowIfCancellationRequested();
        await serial.Send(Encoding.ASCII.GetBytes(command + "\r"));
        long until = Environment.TickCount64 + milliseconds;
        var response = new StringBuilder();
        while (true)
        {
            char ch;
            try { ch = (char)await Read(until); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"The GT did not finish {command} with its ELM prompt.");
            }
            if (ch == '>')
                return response.ToString().Replace(command, "", StringComparison.OrdinalIgnoreCase).Trim();
            if (response.Length >= 4096)
                throw new IOException($"The GT returned too much data for {command}.");
            response.Append(ch);
        }
    }

    private async Task<byte[]> AdapterValue(byte command, byte subcommand)
    {
        byte[] result = await Command(command, [subcommand]);
        if (result.Length < 2 || result[0] != subcommand)
            throw new IOException($"The GT response did not match adapter subcommand {subcommand:X2}.");
        return result[1..];
    }

    private async Task<byte[]> Command(byte command, byte[] data)
    {
        token.ThrowIfCancellationRequested();
        await serial.Send(Encode(command, data));
        long until = Environment.TickCount64 + 1500;
        while (true)
        {
            var (response, payload) = await Packet(until);
            if (response is 0x08 or 0x09)
                continue;
            if (response != command + 0x10)
                throw new IOException($"Unexpected DVI reply {response:X2} for command {command:X2}.");
            return payload;
        }
    }

    private async Task<(byte Command, byte[] Data)> Packet(long until)
    {
        byte command = await Read(until);
        int length = await Read(until);
        int sum = command + length;
        if (command == 0x09)
        {
            int lower = await Read(until);
            sum += lower;
            length = (length << 8) | lower;
        }
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = await Read(until);
            sum += data[i];
        }
        if (((sum + await Read(until)) & 0xFF) != 0xFF)
            throw new IOException("The GT sent a DVI packet with a bad checksum.");
        if (command == 0x7F)
            throw new IOException(data.Length >= 2
                ? $"The GT rejected DVI command {data[0]:X2} (status {data[1]:X2})."
                : "The GT returned a DVI error.");
        return (command, data);
    }

    private async ValueTask<byte> Read(long until)
    {
        while (Environment.TickCount64 < until)
        {
            token.ThrowIfCancellationRequested();
            if (nextByte < buffered)
                return buffer[nextByte++];
            buffered = await serial.Receive(buffer, 0, buffer.Length);
            nextByte = 0;
        }
        throw new TimeoutException("Timed out waiting for the GT or PCM response.");
    }

    private static byte[] Encode(byte command, byte[] payload)
    {
        var packet = new byte[payload.Length + 3];
        packet[0] = command;
        packet[1] = checked((byte)payload.Length);
        payload.CopyTo(packet, 2);
        packet[^1] = unchecked((byte)~packet.Sum(value => (int)value));
        return packet;
    }
}
