using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace WrongWay;

record Reading(long Time, double? Number, string? Text = null)
{
    public string CsvValue => Number?.ToString("G17", CultureInfo.InvariantCulture) ?? Text ?? "";
}

sealed class Channel(int key, int? pid, string name, string unit)
{
    public int Key { get; } = key;
    public int? Pid { get; } = pid;
    public bool IsMath { get; init; }
    public Guid? MathId { get; init; }
    public string Name { get; } = name;
    public string Unit { get; } = unit;
    public string Label => string.IsNullOrEmpty(Unit) ? Name : $"{Name} [{Unit}]";
    public ChannelDisplay? Display { get; set; }
    public string DisplayName => Display?.Name ?? Name;
    public string DisplayUnit => Display?.Unit ?? Unit;
    public string DisplayLabel => DisplayUnit.Length == 0 ? DisplayName : $"{DisplayName} [{DisplayUnit}]";
    public double? DisplayNumber(Reading? reading)
    {
        if (reading?.Number is not double value)
            return null;
        double converted = Display is { } display ? value * display.Scale + display.Offset : value;
        return double.IsFinite(converted) ? converted : null;
    }
    public string FormatDisplay(double value) => Display is { } display
        ? value.ToString($"F{display.Decimals}", CultureInfo.InvariantCulture)
        : value.ToString("0.##", CultureInfo.InvariantCulture);
    public List<Reading> Readings { get; private set; } = [];

    public void Sort() => Readings = Readings.OrderBy(r => r.Time).ToList();

    public int UpperBound(long time)
    {
        int low = 0;
        int high = Readings.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (Readings[mid].Time <= time)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    public Reading? AsOf(long time)
    {
        int index = UpperBound(time) - 1;
        return index < 0 || IsMath && time - Readings[index].Time > 500 ? null : Readings[index];
    }
}

sealed class ScanLog(string name)
{
    public string Name { get; } = name;
    public List<Channel> Channels { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? ScannerVersion { get; set; }
    public int Blocks { get; set; }
    public int Frames { get; private set; }
    public long Start { get; private set; } = long.MaxValue;
    public long End { get; private set; } = long.MinValue;
    public long Duration => Frames == 0 ? 0 : End - Start;

    public void NoteFrame(long time)
    {
        Frames++;
        Start = Math.Min(Start, time);
        End = Math.Max(End, time);
    }

    public Channel? Find(int pid, string name) =>
        Channels.FirstOrDefault(c => c.Pid == pid) ??
        Channels.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static ScanLog Load(string path) =>
        Path.GetExtension(path).Equals(".hpl", StringComparison.OrdinalIgnoreCase)
            ? HplReader.Load(path)
            : CsvLog.Load(path);
}

static class CsvLog
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Escape(string value) => "\""
        + value.Replace("\"", "\"\"") + "\"";

    public static void Export(ScanLog log, string path)
    {
        string target = Path.GetFullPath(path);
        if (File.Exists(target))
            throw new IOException("Choose a new filename to preserve the existing CSV.");
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)))
                Write(log, writer);
            File.Move(temporary, target);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Write(ScanLog log, TextWriter writer)
    {
        writer.WriteLine("Time (s)," + string.Join(",", log.Channels.Select(c => Escape(c.Label))));
        var queue = new PriorityQueue<(int Channel, int Index), (long Time, int Channel)>();
        for (int i = 0; i < log.Channels.Count; i++)
        {
            var readings = log.Channels[i].Readings;
            if (readings.Count != 0)
                queue.Enqueue((i, 0), (readings[0].Time, i));
        }

        var values = new string?[log.Channels.Count];
        var repeated = new List<((int Channel, int Index) Item, (long Time, int Channel) Priority)>();
        while (queue.TryPeek(out _, out var first))
        {
            Array.Clear(values);
            repeated.Clear();
            while (queue.TryPeek(out var item, out var priority) && priority.Time == first.Time)
            {
                queue.Dequeue();
                var readings = log.Channels[item.Channel].Readings;
                values[item.Channel] = Escape(readings[item.Index].CsvValue);
                int next = item.Index + 1;
                if (next >= readings.Count)
                    continue;
                var nextPriority = (readings[next].Time, item.Channel);
                if (nextPriority.Time == first.Time)
                    repeated.Add(((item.Channel, next), nextPriority));
                else
                    queue.Enqueue((item.Channel, next), nextPriority);
            }
            writer.WriteLine((first.Time / 1000d).ToString("0.000", Invariant) + "," +
                string.Join(",", values));
            foreach (var (item, priority) in repeated)
                queue.Enqueue(item, priority);
        }
    }

    public static ScanLog Load(string path)
    {
        using var reader = new TextFieldParser(path);
        reader.SetDelimiters(",");
        reader.HasFieldsEnclosedInQuotes = true;
        var header = reader.ReadFields();
        if (header is null || header.Length < 2)
            throw new InvalidDataException("The CSV needs a time column and at least one channel.");
        int timeColumn = Array.FindIndex(header, field => field.Equals("Time (s)", StringComparison.OrdinalIgnoreCase)
            || field.Equals("Time", StringComparison.OrdinalIgnoreCase)
            || field.Equals("Time (ms)", StringComparison.OrdinalIgnoreCase)
            || field.Equals("Seconds", StringComparison.OrdinalIgnoreCase));
        if (timeColumn < 0)
            throw new InvalidDataException("Use a CSV with a Time (s), Time (ms), Seconds, or Time column.");
        double multiplier = header[timeColumn].EndsWith("(ms)", StringComparison.OrdinalIgnoreCase) ? 1 : 1000;
        var log = new ScanLog(Path.GetFileName(path));
        for (int i = 0; i < header.Length; i++)
        {
            if (i == timeColumn)
                continue;
            string name = header[i];
            int bracket = name.LastIndexOf(" [", StringComparison.Ordinal);
            string unit = bracket >= 0 && name.EndsWith(']') ? name[(bracket + 2)..^1] : "";
            if (bracket >= 0 && unit.Length != 0)
                name = name[..bracket];
            log.Channels.Add(new Channel(i, null, name.Length == 0 ? $"Column {i + 1}" : name, unit));
        }

        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields();
            if (fields is null)
                continue;
            if (fields.Length != header.Length ||
                !double.TryParse(fields[timeColumn], NumberStyles.Float, Invariant, out double time) ||
                !double.IsFinite(time) || Math.Abs(time * multiplier) > 1e12)
                throw new InvalidDataException($"Invalid CSV frame near line {reader.LineNumber - 1}.");
            long milliseconds = (long)Math.Round(time * multiplier);
            log.NoteFrame(milliseconds);
            foreach (var channel in log.Channels)
            {
                string field = fields[channel.Key];
                if (field.Length == 0)
                    continue;
                channel.Readings.Add(ParseReading(milliseconds, field));
            }
        }

        if (log.Frames == 0)
            throw new InvalidDataException("The CSV contains no frames.");
        foreach (var channel in log.Channels)
            channel.Sort();
        return log;
    }

    public static Reading ParseReading(long time, string value) =>
        double.TryParse(value, NumberStyles.Float, Invariant, out double number) && double.IsFinite(number)
            ? new Reading(time, number)
            : new Reading(time, null, value);
}
