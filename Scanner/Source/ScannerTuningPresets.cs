using Avalonia.Controls;
using static WrongWay.DeskUi;
using static WrongWay.GraphStatistic;

namespace WrongWay;

static class ScannerTuningPresets
{
    internal const string AemChannel = "AEM 30-0300 AFR";
    private const string Rpm = "0x000C|RPM~Engine Speed|RPM~Engine RPM|RPM~RPM|RPM";
    private const string Map = "0x000B|kPa~Manifold Absolute Pressure|kPa~Manifold Pressure|kPa~MAP|kPa";
    private const string Tps = "0x0011|%~Throttle Position|%~Throttle|%";
    private const string Ect = "0x0005|°C~ECT Sensor|°C~Coolant Temperature|°C~Engine Coolant Temperature|°C";
    private const string Eq = "Commanded EQ Ratio~Commanded Equivalence Ratio~Target Equivalence Ratio|EQR";
    private const string Maf = "0x0010|g/s~MAF Sensor|g/s~Mass Air Flow|g/s~0x1140|g/sec";
    private const string Stft1 = "0x0006|%~Short Term FT Left Bank|%~STFT Bank 1|%";
    private const string Stft2 = "0x0008|%~Short Term FT Right Bank|%~STFT Bank 2|%";
    private const string Ltft1 = "0x0007|%~Long Term FT Left Bank|%~LTFT Bank 1|%";
    private const string Ltft2 = "0x0009|%~Long Term FT Right Bank|%~LTFT Bank 2|%";
    private const string CommandedAfr = "Commanded AFR|AFR~Target A/F Ratio|:1~6001~Calculated Commanded AFR|AFR";
    private const string Wideband = AemChannel + "|AFR~Measured Wideband AFR|AFR";
    private const string MeasuredLambda = "Measured Lambda";
    private const string MeasuredEq = "Measured EQ Ratio";
    private const string Dfco = "DFCO Active~Decel|Boolean~2517";
    private const string Pe = "Power Enrichment~2513";
    private const string ClosedLoop = "Closed Loop Active~Loop Status|Boolean~2510";
    private const string Knock = "0x11A6|°~Knock Retard Degrees|°~Knock Retard|deg";
    private const string Shift = "Shift Number";
    private const string ShiftSeconds = "Actual Shift Time|s";
    private const string TccApplied = "[TCC Commanded Applied] = 1";
    private const string AcVolts = "7101.10~A/C High Side Pressure Sensor|Volts~A/C High Side Pressure Sensor|V";
    private const string AcMillivolts = "A/C High Side Pressure Sensor|mV";
    private const string AcPressure = "A/C High Side Pressure Sensor|kPa";

    // The pressure branch reverses the project's PID 0x1144 pressure conversion before scaling.
    public static MathSetting AemMath(int input) => new(AemChannel, "AFR", input switch
    {
        0 => AemFormula($"[{AcVolts}]"),
        1 => AemFormula($"([{AcMillivolts}] / 1000)"),
        2 => AemFormula($"((([{AcPressure}] / 6.895 + 14.95) / 1.833) / 51)"),
        _ => throw new ArgumentOutOfRangeException(nameof(input))
    });

    private static string AemFormula(string voltage) =>
        $"if({voltage} >= 0.5 and {voltage} <= 4.5, 2.375 * {voltage} + 7.3125, sqrt(-1))";

    public static MathSetting[] Maths =>
    [
        new("Combined STFT", "%", $"([{Stft1}] + [{Stft2}]) / 2"),
        new("Combined LTFT", "%", $"([{Ltft1}] + [{Ltft2}]) / 2"),
        new("Total Fuel Trims", "%", "[Combined STFT] + [Combined LTFT]"),
        new("Multiplicative Fuel Trims", "%", $"50 * ((1 + [{Stft1}]/100)*(1 + [{Ltft1}]/100) + (1 + [{Stft2}]/100)*(1 + [{Ltft2}]/100) - 2)"),
        new("Bank Fuel Trim Difference", "%", $"[{Stft1}] + [{Ltft1}] - [{Stft2}] - [{Ltft2}]"),
        new("Commanded Lambda", "lambda", $"if([{Eq}] > 0, 1 / [{Eq}], 0)"),
        new("Calculated Commanded AFR", "AFR", $"if([{Eq}] > 0 and [Stoichiometric AFR] > 0, [Stoichiometric AFR] / [{Eq}], 0)"),
        new("Wideband AFR Error", "%", $"if([{Wideband}] > 0 and [{CommandedAfr}] > 0, 100 * ([{Wideband}] / [{CommandedAfr}] - 1), sqrt(-1))"),
        new("Wideband Lambda Error", "%", $"if([Measured Lambda] > 0 and [Commanded Lambda] > 0, 100 * ([Measured Lambda] / [Commanded Lambda] - 1), sqrt(-1))"),
        new("Wideband EQ Error", "%", $"if([{Eq}] > 0 and [{MeasuredEq}] > 0, 100 * ([{Eq}] / [{MeasuredEq}] - 1), sqrt(-1))"),
        new("Injector Duty Cycle", "%", $"[Injector Pulse Width|ms~6200|ms] * [{Rpm}] / 1200"),
        new("Idle RPM Error", "RPM", $"[{Rpm}] - [Desired Idle Speed|RPM]"),
        new("Estimated Cylinder Air Mass", "g/cyl", $"if([{Rpm}] > 0, [{Maf}] * 120 / ([{Rpm}] * 8), sqrt(-1))"),
        new("Knock Occurrence", "%", $"if([{Knock}] > 0, 100, 0)"),
        new("Shift Time Error", "s", $"[{ShiftSeconds}] - [Desired Shift Time|s]")
    ];

    internal sealed record Group(string Name, GraphSetting[] Graphs)
    {
        public override string ToString() => Name;
    }

    private static ChannelSource Source(string token, string unit = "") => new("@channel:" + token, unit);
    private static GraphAxis Axis(string token, string unit, double width) => new(Source(token, unit), width, []);
    private static readonly GraphAxis EngineSpeed = Axis(Rpm, "RPM", 400);
    private static readonly GraphAxis Pressure = Axis(Map, "kPa", 5);
    private static readonly GraphAxis Throttle = Axis(Tps, "%", 10);
    private static readonly GraphAxis Coolant = Axis(Ect + "~ECT Sensor|°F~Coolant Temperature|°F~Engine Coolant Temperature|°F", "°F", 20);
    private static readonly GraphAxis MafFrequency = Axis("0x1250|Hz~MAF Sensor Frequency|Hz~MAF Frequency|Hz", "Hz", 125);
    private static readonly GraphAxis AirMass = Axis("Cylinder Air Mass|g/cyl~Cylinder Airload|g/cyl~Cylinder Air|g/cyl~Estimated Cylinder Air Mass|g/cyl", "g/cyl", 0.04);
    private static readonly GraphAxis Speed = Axis("0x000D~Vehicle Speed Sensor~Vehicle Speed", "mph", 5);
    private static readonly GraphAxis Gear = Axis("Current Gear~Transmission Gear", "", 1);
    private static readonly GraphAxis IdleGear = Axis("Idle Gear State", "", 1);
    private static readonly GraphAxis ShiftNumber = Axis(Shift, "", 1);
    private const string TransmissionTemperature = "4100~Transmissinon Fluid Temperature~Transmission Fluid Temperature";
    private static readonly GraphAxis TransmissionTemp = Axis(TransmissionTemperature, "°F", 20);
    private static readonly GraphAxis SingleRow = new(ChannelSource.SingleRow, 1, [0]);

    private static GraphSetting Graph(string name, GraphAxis x, GraphAxis y, string value,
        GraphStatistic statistic = Mean, string? filter = null, int minimum = 1) =>
        new(x, y, Source(value), statistic, minimum, name, filter);

    public static Group[] Groups()
    {
        string warmSteady = $"[ECT Sensor|°F~Coolant Temperature|°F~Engine Coolant Temperature|°F~Tuning Coolant °F] > 160 and [{Dfco}] = 0 and abs([{Tps}.delta(250)]) < 3 and abs([{Map}.delta(250)]) < 5";
        string part = $"{warmSteady} and [{Pe}] = 0";
        string closed = $"{part} and [{ClosedLoop}] = 1";
        const string fuelError = "Wideband Lambda Error|%~Wideband AFR Error|%~Wideband EQ Error|%";
        const string trim = "Total Fuel Trims|%";
        const string ready = " and [Wideband Ready~AEM 30-0300 Ready] = 1";
        var oil = Graph("Minimum oil pressure", EngineSpeed, Coolant, "Engine Oil Pressure~Engine Oil Pressure Sensor", Minimum);
        var battery = Graph("Minimum battery voltage", EngineSpeed, Axis("Electrical Load|%~Engine Load|%", "%", 10),
            "Control Module Voltage~Battery Voltage~50050", Minimum);
        var imbalance = Graph("Bank trim difference", EngineSpeed, Pressure, "Bank Fuel Trim Difference|%");
        var idc = Graph("Injector duty cycle", EngineSpeed, Pressure, "Injector Duty Cycle|%", Maximum);
        var spark = Graph("Delivered spark", EngineSpeed, AirMass, "50110~Ignition Timing|°~Timing Advance~Spark Advance|°~Spark Advance|deg");
        var knock = Graph("Peak knock retard", EngineSpeed, AirMass, Knock, Maximum);
        var wideMaf = Graph("Wideband MAF correction", MafFrequency, SingleRow, fuelError, filter: part + ready, minimum: 25);
        var closedMaf = Graph("Closed-loop MAF correction", MafFrequency, SingleRow, trim, filter: closed, minimum: 25);
        var wideVe = Graph("Wideband VE correction", EngineSpeed, Pressure, fuelError,
            filter: part + ready + " and [VE Isolated] = 1", minimum: 25);
        var closedVe = Graph("Closed-loop VE correction", EngineSpeed, Pressure, trim,
            filter: closed + " and [VE Isolated] = 1", minimum: 25);
        var pe = Graph("Power enrichment error", EngineSpeed, Pressure, fuelError,
            filter: warmSteady + $" and [{Pe}] = 1" + ready, minimum: 25);
        var o2Left = Graph("Upstream O₂ bank 1", EngineSpeed, Pressure, "0x1145|mV~0x0014|mV~O2 Left Front|mV~O2 Left Upstream|mV");
        var o2Right = Graph("Upstream O₂ bank 2", EngineSpeed, Pressure, "0x1148|mV~0x0018|mV~O2 Right Front|mV~O2 Right Upstream|mV");
        var transTemp = Graph("Maximum transmission temperature", Speed, Gear, TransmissionTemperature, Maximum);
        var misfires = Enumerable.Range(1, 8).Select(cylinder =>
            Graph($"Misfire count increase · cylinder {cylinder}", EngineSpeed, SingleRow, $"Misfire Increase Cyl {cylinder}", Maximum));

        return
        [
            new("First Start", [oil, battery, o2Left, o2Right, imbalance, idc]),
            new("Idle Tuning", [
                Graph("Base running airflow", Coolant, IdleGear, "Base Running Airflow|g/s~Desired IAC Airflow|g/s~2220|g/s", filter: "[Idle Active] = 1"),
                Graph("Idle airflow correction", Coolant, IdleGear, "Idle Airflow Trim|g/s", filter: "[Idle Active] = 1"),
                Graph("Idle RPM error", Coolant, IdleGear, "Idle RPM Error|RPM", filter: "[Idle Active] = 1")]),
            new("MAF Tuning", [wideMaf, closedMaf]),
            new("Speed Density", [wideVe, closedVe]),
            new("Street Tuning", [closedMaf, closedVe, imbalance, o2Left, o2Right]),
            new("WOT / Dyno", [pe, spark, knock, Graph("Average knock retard", EngineSpeed, AirMass, Knock),
                Graph("Knock occurrence", EngineSpeed, AirMass, "Knock Occurrence|%"), idc]),
            new("Transmission", [
                .. Enumerable.Range(1, 3).Select(shift => Graph($"{shift}–{shift + 1} shift time", Throttle, TransmissionTemp,
                    ShiftSeconds, filter: $"[{Shift}] = {shift} and [Shift Event] = 1")),
                Graph("Shift time error", Throttle, ShiftNumber, "Shift Time Error|s", filter: "[Shift Event] = 1"),
                Graph("Commanded shift pressure", Axis("Engine Torque", "lb-ft", 25), ShiftNumber, "Commanded Shift Pressure", filter: "[Shift Event] = 1"),
                Graph("Shift speed", Throttle, ShiftNumber, "Shift Speed|mph", filter: "[Shift Event] = 1"),
                Graph("Shift RPM", Throttle, ShiftNumber, "Shift RPM|RPM", filter: "[Shift Event] = 1"),
                transTemp]),
            new("Torque Converter", [
                Graph("Applied TCC slip", Speed, Throttle, "TCC Slip|RPM~Torque Converter Slip|RPM", filter: TccApplied),
                Graph("TCC PWM duty cycle", Speed, Throttle, "TCC Duty Cycle|%~TCC PWM|%"), transTemp]),
            new("Advanced Diagnostics", [oil, battery, o2Left, o2Right, imbalance, .. misfires,
                Graph("Maximum intake air temperature", Speed, Coolant, "0x000F~IAT Sensor~Intake Air Temperature", Maximum),
                Graph("MAP stability", EngineSpeed, Throttle, Map, StandardDeviation),
                Graph("MAF stability", EngineSpeed, Throttle, Maf, StandardDeviation)])
        ];
    }

    public static (Channel Clock, Func<Reading, long, double?> Calculate)? DelayedWideband(ScanLog log, Channel value)
    {
        // Only built-in corrections are paired here. Custom value channels retain ordinary timestamp shifting.
        var pair = value.Name switch
        {
            "Wideband AFR Error" => (Measured: Wideband, Commanded: CommandedAfr, Invert: false),
            "Wideband Lambda Error" => (Measured: MeasuredLambda, Commanded: "Commanded Lambda", Invert: false),
            "Wideband EQ Error" => (Measured: MeasuredEq, Commanded: Eq, Invert: true),
            _ => default
        };
        if (!value.IsMath || pair.Measured is null ||
            MathChannel.Resolve(log.Channels, pair.Measured, includeMath: true) is not { } measured ||
            MathChannel.Resolve(log.Channels, pair.Commanded!, includeMath: true) is not { } commanded)
            return null;

        return (measured, (reading, engineTime) =>
        {
            var target = commanded.AsOf(engineTime);
            if (target?.Number is not > 0 || engineTime - target.Time > 500 || reading.Number is not > 0)
                return null;
            double actual = reading.Number.Value;
            double requested = target.Number.Value;
            return value.DisplayNumber(new Reading(engineTime,
                100 * ((pair.Invert ? requested / actual : actual / requested) - 1)));
        });
    }

    internal static void SelfTest()
    {
        var settings = RequiredMaths().Append(AemMath(0)).ToArray();
        if (!MathSetting.Validate(settings))
            throw new InvalidDataException("The P59 preset math library is invalid.");
        foreach (var group in Groups())
            if (!(ScannerLayout.Default with { Graph = group.Graphs[0], Graphs = group.Graphs, Maths = settings }).IsValid())
                throw new InvalidDataException($"The P59 {group.Name} histogram group is invalid.");

        var log = new ScanLog("P59 preset fixture");
        Channel Add(int? pid, string name, string unit, double? value, string? text = null)
        {
            var channel = new Channel(log.Channels.Count, pid, name, unit);
            channel.Readings.Add(new Reading(0, value, text));
            log.Channels.Add(channel);
            return channel;
        }
        Add(5, "ECT Sensor", "°C", 85);
        Add(17, "Throttle Position", "%", 12);
        Add(11, "Manifold Absolute Pressure", "kPa", 50);
        Add(0x1105, "Decel", "Boolean", null, "Inactive");
        Add(0x1105, "Loop Status", "Boolean", null, "Closed");
        Add(0x1922, "Power Enrichment", "Boolean", null, "Inactive");
        Add(0x1250, "MAF Sensor Frequency", "Hz", 3000);
        Add(6, "Short Term FT Left Bank", "%", 2);
        Add(8, "Short Term FT Right Bank", "%", 4);
        Add(7, "Long Term FT Left Bank", "%", 8);
        Add(9, "Long Term FT Right Bank", "%", 10);
        var commanded = Add(0x119E, "Target A/F Ratio", ":1", 12.5);
        commanded.Readings.Add(new Reading(100, 13));
        Add(null, "Wideband Ready", "Boolean", 1);
        var voltage = Add(0x1144, "A/C High Side Pressure Sensor", "Volts", 2.5);
        voltage.Readings.AddRange([new Reading(100, 2.5), new Reading(200, 0)]);
        MathChannel.Build(log, settings);
        var maf = Groups().Single(group => group.Name == "MAF Tuning").Graphs;
        var frequency = maf[0].X.Source.Resolve(log)!;
        var afrError = maf[0].Value!.Resolve(log)!;
        var filtered = MathChannel.BuildFilter(log, maf[0].FilterExpression)!;
        if (!filtered(0) || afrError.AsOf(0)?.Number is not double error || Math.Abs(error - 6) > 0.00001 ||
            afrError.AsOf(200)?.Number is not null || maf[1].Value!.Resolve(log)?.AsOf(0)?.Number != 12 ||
            MathChannel.BuildFilter(log, maf[1].FilterExpression)?.Invoke(0) != true)
            throw new InvalidDataException("P59 wideband, closed-loop, or fuel trim calculations failed.");

        var samples = new HistogramSamples(maf[0], frequency, frequency, afrError, filtered);
        if (!samples.AddSamples(0) || !samples.Cells.TryGetValue((3000, 0), out var first) || first.Count != 1)
            throw new InvalidDataException("P59 single-row MAF binning failed.");

        var pair = DelayedWideband(log, afrError);
        if (pair is null || pair.Value.Calculate(pair.Value.Clock.AsOf(100)!, 0) is not double aligned || Math.Abs(aligned - 6) > 0.00001 ||
            pair.Value.Calculate(pair.Value.Clock.AsOf(100)!, 100) == aligned)
            throw new InvalidDataException("Wideband compensation did not pair measured AFR with the earlier command.");

        Console.WriteLine("P59 preset groups, Boolean filters, AEM scaling, delay alignment and single-row MAF checks passed.");
    }

    public static IEnumerable<MathSetting> RequiredMaths()
    {
        yield return new("Tuning Coolant °F", "°F", $"[{Ect}] * 1.8 + 32");
        yield return new("AEM 30-0300 Ready", "Boolean", $"[{AemChannel}|AFR] > 0");
        foreach (var math in Maths)
            yield return math;
        for (int cylinder = 1; cylinder <= 8; cylinder++)
        {
            string counter = $"Misfire Current Cyl {cylinder}";
            yield return new($"Misfire Increase Cyl {cylinder}", "count", $"max(0, [{counter}.delta(0)])");
        }
    }
}

sealed partial class ScannerWindow
{
    private async Task LoadHistogramPresets()
    {
        var groups = ScannerTuningPresets.Groups();
        var group = Choice(groups, 0);
        var input = Choice(new[] { "Keep current settings", "A/C input (V)", "A/C input (mV)", "A/C pressure (kPa)" }, 0);
        var fields = Fields(("Preset group", group), ("AEM 30-0300 input", input));
        fields.Children.Add(Text("Loading a group replaces the active histogram tables. Save the layout first to keep those tables. " +
            "Enable the AEM conversion after verifying the logged signal against the gauge.", 12, Muted));
        var next = await Form(this, "P59 · OS 12587811 histogram presets", fields, () =>
        {
            if (group.SelectedItem is not ScannerTuningPresets.Group selected)
                throw new FormatException("Select a preset group.");

            var maths = (scannerLayout.Maths ?? MathSetting.Defaults).ToList();
            foreach (var setting in ScannerTuningPresets.RequiredMaths())
            {
                if (!maths.Any(existing => existing.Name.Trim().Equals(setting.Name, StringComparison.OrdinalIgnoreCase)))
                    maths.Add(setting);
            }
            if (input.SelectedIndex > 0)
            {
                maths.RemoveAll(existing => existing.Name.Trim().Equals(ScannerTuningPresets.AemChannel, StringComparison.OrdinalIgnoreCase));
                maths.Add(ScannerTuningPresets.AemMath(input.SelectedIndex - 1));
            }
            if (!MathSetting.Validate(maths.ToArray()))
                throw new FormatException($"Preset math exceeds the {MathSetting.Maximum}-channel limit or conflicts with custom math.");
            return scannerLayout with { Graph = selected.Graphs[0], Graphs = selected.Graphs, Maths = maths.ToArray() };
        }, height: 350);
        if (next is null)
            return;

        selectedGraphIndex = 0;
        if (ApplyLayout(next, $"Loaded {groups[group.SelectedIndex].Name} histograms."))
            ShowHistogram();
    }
}
