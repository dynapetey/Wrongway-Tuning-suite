using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace WrongWay;

static class HplReader
{
    const int ClockPeriod = 1 << 24;
    const int MaxBlockBytes = 64 * 1024 * 1024;
    const int MaxFileBytes = 512 * 1024 * 1024;
    const int TextKind = 2816;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Dictionary<int, string> Names = new()
    {
        [3] = "Fuel System Status", [5] = "Coolant Temperature",
        [6] = "Short-Term Fuel Trim B1", [7] = "Long-Term Fuel Trim B1",
        [8] = "Short-Term Fuel Trim B2", [9] = "Long-Term Fuel Trim B2",
        [11] = "Manifold Pressure", [12] = "Engine RPM", [13] = "Vehicle Speed",
        [14] = "Spark Advance", [15] = "Intake Air Temperature", [16] = "MAF Airflow",
        [17] = "Throttle Position", [2100] = "Module Voltage", [2301] = "MAF Frequency",
        [2630] = "Knock Retard", [6001] = "Commanded AFR", [6010] = "Commanded Equivalence Ratio",
        [6100] = "O2 Sensor B1", [6102] = "O2 Sensor B2",
        [6200] = "Injector Pulse Width B1", [6201] = "Injector Pulse Width B2",
        [7101] = "A/C Pressure Sensor Input"
    };

    public static ScanLog Load(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException("This HPL exceeds the 512 MiB file limit.");
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static ScanLog Parse(ReadOnlySpan<byte> data, string name)
    {
        if (data.StartsWith("HPT "u8))
            throw new InvalidDataException("This legacy HPT-wrapped log uses a different layout. This build opens compact SS/SYNC HPL logs.");
        if (data.Length < 35 || !data.StartsWith("SS\0\0SYNC"u8) ||
            !data.Slice(14, 4).SequenceEqual("SC\0\0"u8))
            throw new InvalidDataException("This file does not have a supported compact HPL header.");

        var log = new ScanLog(name);
        var channels = new Dictionary<int, (Channel Channel, int Kind)>();
        int count = U16(data, 33);
        int position = 35;
        if (count is < 1 or > 128)
            throw new InvalidDataException("Invalid HPL channel count.");
        for (int i = 0; i < count; i++)
        {
            if (data.Length - position < 24)
                throw new InvalidDataException("Truncated HPL channel table.");
            int key = U16(data, position);
            int pid = U16(data, position + 2);
            int kind = U16(data, position + 4);
            int length = data[position + 23];
            position += 24;
            if (data.Length - position < length || key >= 128 || channels.ContainsKey(key))
                throw new InvalidDataException("Invalid HPL channel definition.");
            string unit = StrictUtf8.GetString(data.Slice(position, length));
            position += length;
            var channel = new Channel(key, pid, Names.GetValueOrDefault(pid, $"PID {pid}"), unit);
            channels.Add(key, (channel, kind));
            log.Channels.Add(channel);
        }

        position += ReadMetadata(data[position..], log);
        long? lastBlockStart = null;
        while (position < data.Length)
        {
            int marker = data[position..].IndexOf("CDG\0"u8);
            if (marker < 0)
                break;
            int offset = position + marker;
            if (data.Length - offset < 12)
            {
                log.Warnings.Add($"Truncated data group header at byte {offset}.");
                break;
            }
            int compressed = I32(data, offset + 4);
            int expanded = I32(data, offset + 8);
            position = offset + 12;
            if (compressed < 1 || expanded < 2 || expanded > MaxBlockBytes)
            {
                log.Warnings.Add($"Invalid data group size at byte {offset}.");
                continue;
            }
            if (compressed > data.Length - position)
            {
                log.Warnings.Add($"Truncated data group at byte {offset}. Earlier complete groups are available.");
                break;
            }

            try
            {
                var block = Inflate(data.Slice(position, compressed), expanded);
                var (frameTimes, updates, blockStart) = ReadBlock(block, channels, lastBlockStart);
                foreach (long time in frameTimes)
                    log.NoteFrame(time);
                foreach (var (channel, reading) in updates)
                    channel.Readings.Add(reading);
                lastBlockStart = blockStart;
                log.Blocks++;
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
            {
                log.Warnings.Add($"Skipped data group at byte {offset}. {ex.Message}");
            }
            position += compressed;
        }

        if (log.Frames == 0 || log.Channels.All(c => c.Readings.Count == 0))
            throw new InvalidDataException("No complete, supported HPL frames were decoded.");
        foreach (var channel in log.Channels)
            channel.Sort();
        return log;
    }

    private static (List<long>, List<(Channel, Reading)>, long) ReadBlock(
        ReadOnlySpan<byte> block, Dictionary<int, (Channel Channel, int Kind)> channels, long? previousStart)
    {
        int records = U16(block, 0);
        if (records < 1 || records > (block.Length - 2) / 4)
            throw new InvalidDataException("Invalid frame count.");
        var times = new List<long>(records);
        var updates = new List<(Channel, Reading)>();
        int position = 2;
        long start = Unwrap(U24(block, position), previousStart);
        for (int i = 0; i < records; i++)
        {
            if (block.Length - position < 4)
                throw new InvalidDataException("Incomplete frame header.");
            long time = Unwrap(U24(block, position), start);
            int count = block[position + 3];
            position += 4;
            if (count < 1 || count > channels.Count)
                throw new InvalidDataException("Invalid frame channel count.");
            times.Add(time);
            for (int j = 0; j < count; j++)
            {
                if (position == block.Length)
                    throw new InvalidDataException("Incomplete frame value.");
                int tag = block[position++];
                if (!channels.TryGetValue(tag & 127, out var definition))
                    throw new InvalidDataException("Undefined channel in frame.");
                if ((tag & 128) != 0)
                    continue;
                if (definition.Kind == TextKind)
                {
                    if (position == block.Length)
                        throw new InvalidDataException("Incomplete text value.");
                    int length = block[position++];
                    if (length > block.Length - position)
                        throw new InvalidDataException("Truncated text value.");
                    string value = StrictUtf8.GetString(block.Slice(position, length));
                    position += length;
                    updates.Add((definition.Channel, new Reading(time, null, value)));
                }
                else
                {
                    if (block.Length - position < 8)
                        throw new InvalidDataException("Truncated numeric value.");
                    double value = BinaryPrimitives.ReadDoubleLittleEndian(block[position..]);
                    position += 8;
                    if (double.IsFinite(value))
                        updates.Add((definition.Channel, new Reading(time, value)));
                }
            }
        }
        if (position != block.Length)
            throw new InvalidDataException("Unexpected bytes after the final frame.");
        return (times, updates, start);
    }

    private static long Unwrap(int time, long? anchor)
    {
        if (anchor is null)
            return time;
        return time + ClockPeriod * (long)Math.Round((anchor.Value - time) / (double)ClockPeriod);
    }

    private static int ReadMetadata(ReadOnlySpan<byte> data, ScanLog log)
    {
        // In the compact layout the last channel's "%" unit can precede this marker.
        if (data.Length < 19 || !data.StartsWith("MET\0"u8))
            return 0;
        int compressed = I32(data, 11);
        int expanded = I32(data, 15);
        if (compressed < 1 || compressed > data.Length - 19 || expanded < 1 || expanded > MaxBlockBytes)
            return 0;
        try
        {
            string text = Encoding.Latin1.GetString(Inflate(data.Slice(19, compressed), expanded));
            var version = Regex.Match(text, @"(?<![\d.])\d+\.\d+\.\d+(?![\d.])");
            if (version.Success)
                log.ScannerVersion = version.Value;
        }
        catch (InvalidDataException)
        {
            log.Warnings.Add("Optional log metadata could not be read.");
        }
        return 19 + compressed;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int size)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var buffer = new byte[size];
        int length = deflate.ReadAtLeast(buffer, buffer.Length, false);
        if (length != size || deflate.ReadByte() != -1)
            throw new InvalidDataException("Data group size disagrees with its header.");
        return buffer;
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
    private static int I32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
    private static int U24(ReadOnlySpan<byte> data, int at) => data[at] | data[at + 1] << 8 | data[at + 2] << 16;
}
