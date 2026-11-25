using System.Text.Json;

namespace WrongWay;

sealed record ChannelSource(string Name, string Unit = "", int Occurrence = 0, Guid? MathId = null, int? VcmId = null)
{
    public static readonly ChannelSource Rpm = new("@rpm");
    public static readonly ChannelSource Map = new("@map");
    public static readonly ChannelSource Throttle = new("@throttle");

    public Channel? Resolve(ScanLog? log)
    {
        if (log is null)
            return null;
        if (MathId is { } id)
            return log.Channels.FirstOrDefault(channel => channel.MathId == id);
        if (VcmId is { } vcmId)
        {
            var candidates = log.Channels.Where(channel => VcmConfigImporter.MatchesChannel(channel, vcmId) &&
                (Unit.Length == 0 || VcmConfigImporter.SameUnit(channel.Unit, Unit))).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        return Name switch
        {
            "@rpm" => log.Find(12, "RPM") ?? log.Find(12, "Engine Speed") ?? log.Find(50070, "RPM"),
            "@map" => log.Find(11, "Manifold Absolute Pressure") ?? log.Find(11, "Manifold Pressure") ?? log.Find(11, "MAP") ?? log.Find(50030, "MAP"),
            "@throttle" => log.Find(17, "Throttle") ?? log.Find(50090, "Throttle"),
            _ => log.Channels.Where(channel => channel.Name == Name && channel.Unit == Unit)
                .Skip(Occurrence).FirstOrDefault()
        };
    }

    public static ChannelSource From(ScanLog log, Channel channel) =>
        new(channel.Name, channel.Unit, log.Channels.TakeWhile(item => item != channel)
            .Count(item => item.Name == channel.Name && item.Unit == channel.Unit), channel.MathId);

    public override string ToString() => Name switch
    {
        "@rpm" => "Engine speed (automatic)",
        "@map" => "Manifold pressure (automatic)",
        "@throttle" => "Throttle position (automatic)",
        _ => (Unit.Length == 0 ? Name : $"{Name} [{Unit}]") + (Occurrence == 0 ? "" : $" #{Occurrence + 1}")
    };
}

enum GaugeKind { Dial, Bar, Digital }
enum GraphStatistic { Mean, Minimum, Maximum, Count }

sealed record GaugeSetting(ChannelSource Source, string Label, GaugeKind Kind, double Minimum, double Maximum, int Color)
{
    public override string ToString() => $"{Label}   ·   {Kind}   ·   {Minimum:G} to {Maximum:G}";
}

sealed record TraceSetting(ChannelSource Source, int Color, double? Minimum = null, double? Maximum = null)
{
    public override string ToString() => Minimum is null ? $"{Source}   ·   Auto scale" : $"{Source}   ·   {Minimum:G} to {Maximum:G}";
}

sealed record ChannelDisplay(ChannelSource Source, string Name, string Unit, double Scale, double Offset, int Decimals, bool Hidden)
{
    public override string ToString() => $"{Name}   ·   {Source}" + (Hidden ? "   ·   Hidden" : "");
}

sealed record GraphAxis(ChannelSource Source, double Width, double[] Breakpoints)
{
    public double Bin(double value)
    {
        if (Breakpoints.Length == 0)
            return Math.Floor(value / Width) * Width;
        int index = Array.BinarySearch(Breakpoints, value);
        if (index >= 0)
            return Breakpoints[index];
        int right = ~index;
        if (right == 0 || right == Breakpoints.Length)
            return Breakpoints[Math.Min(right, Breakpoints.Length - 1)];
        return value - Breakpoints[right - 1] <= Breakpoints[right] - value
            ? Breakpoints[right - 1] : Breakpoints[right];
    }
}

sealed record GraphSetting(GraphAxis X, GraphAxis Y, ChannelSource? Value, GraphStatistic Statistic, int MinimumSamples,
    string? Label = null, string? FilterExpression = null)
{
    public static readonly GraphSetting Default = new(new(ChannelSource.Rpm, 500, []), new(ChannelSource.Map, 10, []),
        null, GraphStatistic.Mean, 1, "Primary Histogram", null);

    public override string ToString() =>
        (string.IsNullOrWhiteSpace(Label) ? $"{X.Source} × {Y.Source}" : Label) +
        (string.IsNullOrWhiteSpace(FilterExpression) ? "" : $"   ·   Filter: {FilterExpression}");
}

sealed record LiveChannelSetting(string Id, string Unit, string Label)
{
    public override string ToString() => $"{Label} [{Unit}]   ·   {Id}";
}

sealed record ScannerLayout(GaugeSetting[] Gauges, TraceSetting[]? Traces, GraphSetting Graph,
    ChannelDisplay[] Channels, LiveChannelSetting[]? LiveChannels, MathSetting[]? Maths = null, GraphSetting[]? Graphs = null)
{
    public const int MaximumTraces = 32;
    public const int ColorCount = 32;

    public static ScannerLayout Default => new(
        [new(ChannelSource.Rpm, "Engine speed", GaugeKind.Dial, 0, 8000, 0),
         new(ChannelSource.Map, "Manifold pressure", GaugeKind.Dial, 0, 120, 1),
         new(ChannelSource.Throttle, "Throttle position", GaugeKind.Dial, 0, 100, 2)],
        null, GraphSetting.Default, [], null);

    public static string DefaultPath => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
        ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "wrongway-suite", "scanner-layout.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ScannerLayout Load(string path)
    {
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return VcmConfigImporter.ImportLayoutXml(path, Default);

        using var stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024)
            throw new InvalidDataException("The scanner layout is larger than 256 KB.");
        var layout = JsonSerializer.Deserialize<ScannerLayout>(stream, Json);
        if (layout is null || !layout.IsValid())
            throw new InvalidDataException("The file is not a valid WrongWay scanner layout.");
        return layout;
    }

    public void Save(string path)
    {
        if (!IsValid())
            throw new InvalidDataException("Some scanner layout settings are invalid.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
                JsonSerializer.Serialize(stream, this, Json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void SelfTest()
    {
        var log = new ScanLog("test");
        var x = new Channel(1, null, "X", "V");
        var y = new Channel(2, null, "Y", "%");
        var value = new Channel(3, null, "X", "V");
        log.Channels.AddRange([x, y, value]);
        var source = ChannelSource.From(log, value);
        if (source.Resolve(log) != value || source.Occurrence != 1)
            throw new InvalidDataException("Duplicate channel binding failed.");
        var display = new ChannelDisplay(source, "AFR", "AFR", 2, 9.57, 2, false);
        var settings = new GraphSetting(new(ChannelSource.From(log, x), 10, []),
            new(ChannelSource.From(log, y), 1, [0, 10, 20]), source, GraphStatistic.Mean, 1, "AFR vs X/Y", "[Y] >= 10");
        var layout = Default with { Graph = settings, Graphs = [settings], Channels = [display] };
        string path = Path.Combine(Path.GetTempPath(), "wrongway-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            layout.Save(path);
            var loaded = Load(path);
            var expanded = Enumerable.Range(0, MaximumTraces)
                .Select(i => new TraceSetting(new ChannelSource("Trace " + i), i)).ToArray();
            var expandedLayout = layout with { Traces = expanded };
            expandedLayout.Save(path);
            var expandedReloaded = Load(path);
            if (!expandedReloaded.Traces!.SequenceEqual(expanded)
                || (expandedLayout with { Traces = expanded.Append(new TraceSetting(new ChannelSource("Overflow"), 0)).ToArray() }).IsValid()
                || (expandedLayout with { Traces = [expanded[0], expanded[0]] }).IsValid()
                || (expandedLayout with { Traces = [expanded[0] with { Color = ColorCount }] }).IsValid())
                throw new InvalidDataException("32-trace round-trip, limit, duplicate or color validation failed.");
            value.Display = loaded.Channels.Single();
            x.Readings.Add(new Reading(0, -1));
            y.Readings.Add(new Reading(0, 12));
            value.Readings.AddRange([new(0, 2), new(50, 3), new(600, 4)]);
            var histogram = new HistogramSamples(loaded.Graph, x, y, value, MathChannel.BuildFilter(log, loaded.Graph.FilterExpression));
            if (!histogram.AddSamples() || histogram.AddSamples()
                || !histogram.Cells.TryGetValue((-10, 10), out var cell) || cell.Count != 2
                || Math.Abs(cell.Mean - 14.57) > 0.000001 || value.Readings[0].CsvValue != "2")
                throw new InvalidDataException("Layout round-trip or histogram conversion failed.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public bool IsValid() =>
        Gauges is { Length: <= 12 } && Gauges.All(g => g is not null && Source(g.Source) && Text(g.Label)
            && Enum.IsDefined(g.Kind) && Range(g.Minimum, g.Maximum) && Color(g.Color))
        && (Traces is null || Traces.Length <= MaximumTraces && Traces.All(t => t is not null && Source(t.Source) && Color(t.Color)
            && (t.Minimum is null && t.Maximum is null || t.Minimum is double low && t.Maximum is double high && Range(low, high)))
            && Traces.Select(t => t.Source).Distinct().Count() == Traces.Length)
        && ValidGraph(Graph)
        && (Graphs is null || Graphs.Length <= 16 && Graphs.All(ValidGraph))
        && Channels is { Length: <= 128 } && Channels.All(c => c is not null && Source(c.Source) && Text(c.Name)
            && c.Unit is { Length: <= 80 } && double.IsFinite(c.Scale) && c.Scale != 0 && double.IsFinite(c.Offset) && c.Decimals is >= 0 and <= 6)
        && Channels.Select(c => c.Source).Distinct().Count() == Channels.Length
        && (LiveChannels is null || LiveChannels.Length is >= 1 and <= LiveScanner.MaximumLiveBytes
            && LiveChannels.All(c => c is not null && Text(c.Id) && Text(c.Label) && c.Unit is { Length: <= 80 })
            && LiveChannels.Select(c => c.Id).Distinct().Count() == LiveChannels.Length)
        && (Maths is null || MathSetting.Validate(Maths));

    private static bool ValidGraph(GraphSetting? g)
    {
        if (g is null || !Axis(g.X) || !Axis(g.Y) || (g.Value is not null && !Source(g.Value))
            || !Enum.IsDefined(g.Statistic) || g.MinimumSamples is < 1 or > 1_000_000
            || (g.Label is not null && g.Label.Length > 120))
            return false;
        if (!string.IsNullOrWhiteSpace(g.FilterExpression))
        {
            try { _ = new MathFormula(g.FilterExpression); }
            catch (FormatException) { return false; }
        }
        return true;
    }

    private static bool Axis(GraphAxis? axis) => axis is not null && Source(axis.Source) &&
        double.IsFinite(axis.Width) && axis.Width > 0 && axis.Breakpoints is { Length: <= 120 }
        && axis.Breakpoints.All(double.IsFinite)
        && axis.Breakpoints.Zip(axis.Breakpoints.Skip(1), (a, b) => a < b).All(increasing => increasing);
    private static bool Source(ChannelSource? source) => source is not null && Text(source.Name)
        && source.Unit is { Length: <= 80 } && source.Occurrence is >= 0 and <= 1024;
    private static bool Text(string? value) => value is { Length: > 0 and <= 250 };
    private static bool Color(int index) => index is >= 0 and < ColorCount;
    private static bool Range(double low, double high) => double.IsFinite(low) && double.IsFinite(high - low) && high > low;
}
