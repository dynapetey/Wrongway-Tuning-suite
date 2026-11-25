// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace WrongWay;

sealed record VcmParameterMap(int VcmId, int? ObdPid, string CatalogId, string Name, string DefaultUnit, string[] Aliases);

static class VcmConfigImporter
{
    private static readonly VcmParameterMap[] ParameterMaps =
    [
        new(3, 0x0003, "FuelingMode", "Fuel System Status", "Raw", ["Fueling Mode", "Fuel System Status"]),
        new(4, 0x0004, "EngineLoad", "Engine Load", "%", ["Calculated Engine Load"]),
        new(5, 0x0005, "ECTSensor", "Coolant Temperature", "°F", ["ECT Sensor", "Engine Coolant Temp", "ECT"]),
        new(6, 0x0006, "ShortTermFTLeftBank", "Short-Term Fuel Trim B1", "%", ["Short Term FT Left Bank", "STFT B1", "ST Fuel Trim"]),
        new(7, 0x0007, "LongTermFTLeftBank", "Long-Term Fuel Trim B1", "%", ["Long Term FT Left Bank", "LTFT B1", "LT Fuel Trim"]),
        new(8, 0x0008, "ShortTermFTRightBank", "Short-Term Fuel Trim B2", "%", ["Short Term FT Right Bank", "STFT B2"]),
        new(9, 0x0009, "LongTermFTRightBank", "Long-Term Fuel Trim B2", "%", ["Long Term FT Right Bank", "LTFT B2"]),
        new(10, 0x114E, "FuelTankPressureSensorVoltage", "Fuel Pressure", "kPa", ["Fuel Tank Pressure Sensor Voltage"]),
        new(11, 0x000B, "MAPSensor", "Manifold Pressure", "kPa", ["MAP Sensor", "Manifold Absolute Pressure", "MAP"]),
        new(12, 0x000C, "EngineSpeed", "Engine RPM", "RPM", ["Engine Speed", "RPM"]),
        new(13, 0x000D, "VehicleSpeedSensor", "Vehicle Speed", "mph", ["Vehicle Speed Sensor", "VSS", "Speed"]),
        new(14, 0x000E, "IgnitionTiming", "Spark Advance", "°", ["Ignition Timing", "Spark"]),
        new(15, 0x000F, "IATSensor", "Intake Air Temperature", "°F", ["IAT Sensor", "IAT"]),
        new(16, 0x0010, "MAFSensor", "MAF Airflow", "g/s", ["MAF Sensor", "Mass Air Flow", "MAF"]),
        new(17, 0x0011, "ThrottlePosition", "Throttle Position", "%", ["TP Sensor", "TPS"]),
        new(20, 0x1145, "O2LeftFront", "O2 Sensor B1S1", "mV", ["O2 Left Front", "O2 Sensor B1", "O2LeftUpstream"]),
        new(24, 0x1148, "O2RightFront", "O2 Sensor B2S1", "mV", ["O2 Right Front", "O2 Sensor B2", "O2RightUpstream"]),
        new(51, 0x119D, "BarometricPressuresampled", "Barometric Pressure", "kPa", ["Barometric Pressure (sampled)"]),
        new(52, 0x1146, "O2LeftRear", "O2 Sensor B1S2", "mV", ["O2 Left Rear", "O2LeftDownstream"]),
        new(56, 0x1149, "O2RightRear", "O2 Sensor B2S2", "mV", ["O2 Right Rear", "O2RightDownstream"]),
        new(66, 0x1141, "Ignition1Signal", "Control Module Voltage", "Volts", ["Ignition 1 Signal", "Module Voltage"]),
        new(67, null, "RamLoad", "Absolute Load", "g/cyl", ["Load", "Load (from RPM and MAF)"]),
        new(68, 0xFC05, "TargetEquivalenceRatio", "Commanded Equivalence Ratio", "EQR", ["Commanded Equivalence Ratio", "Target A/F Ratio"]),
        new(69, 0x131E, "TPDesiredAngle", "Relative Throttle Position", "%", ["TP Desired Angle", "TP Indicated Angle"]),
        new(70, 0x000F, "IATSensor", "Ambient Air Temperature", "°F", ["IAT Sensor"]),
        new(73, 0x12B0, "AcceleratorPedalPosition", "Accelerator Pedal Position D", "%", ["Accelerator Pedal Position", "APP Indicated Angle"]),
        new(76, 0x131E, "TPDesiredAngle", "Commanded Throttle Actuator", "%", ["TP Desired Angle"]),
        new(82, 0x12F3, "EthanolContent", "Alcohol Fuel Percentage", "%", ["Ethanol Content"]),
        new(2100, 0x1141, "Ignition1Signal", "Module Voltage", "Volts", ["Ignition 1 Signal", "Control Module Voltage"]),
        new(2107, 0x115C, "EngineOilPressure", "Engine Oil Pressure", "kPa", ["Engine Oil Pressure Sensor"]),
        new(2110, 0x1142, "MAPSensorVolts", "MAP Sensor Voltage", "Volts", ["MAP Sensor"]),
        new(2114, 0x1144, "ACHighSidePressureSensor", "A/C Pressure Sensor Voltage", "Volts", ["A/C Pressure Sensor Input"]),
        new(2128, 0x12F3, "EthanolContent", "Ethanol Content", "%", ["Alcohol Fuel Percentage"]),
        new(2146, 0x119F, "EngineOilLifeRemaining", "Engine Oil Life Remaining", "%", []),
        new(2154, null, "RamLoad", "Cylinder Airload", "g/cyl", ["Load", "Load (from RPM and MAF)"]),
        new(2210, 0x1192, "DesiredIdleSpeed", "Desired Idle Speed", "RPM", []),
        new(2220, 0x1617, "DesiredIACAirflow", "Desired Idle Airflow", "g/s", ["Desired IAC Airflow", "Desired Idle Airflow Base"]),
        new(2222, 0x1179, "CurrentIACPosition", "Current IAC Position", "Steps", []),
        new(2223, 0x1176, "LearnedIACPosition", "Learned IAC Position", "Steps", []),
        new(2234, null, "RamThrottleFollowerAirflow", "Throttle Follower Airflow", "g/s", ["Throttle Cracker Airflow"]),
        new(2240, 0x12F7, "DesiredIACPosition", "Desired IAC Position", "Counts", []),
        new(2301, 0x1250, "MAFSensorFrequency", "MAF Frequency", "Hz", ["MAF Sensor Frequency"]),
        new(2311, null, "RamMafRationalityTestSD", "Dynamic Airflow", "g/s", ["MAF Rationality Test - SpeedDensity", "MAF Airflow", "MAF Sensor"]),
        new(2320, null, "RamMafRationalityTestSD", "Dynamic Cylinder Air / Airflow", "g/s", ["MAF Rationality Test - SpeedDensity", "Load (from RPM and MAF)", "MAF Airflow"]),
        new(2321, null, "RamLoad", "Cylinder Air", "g/cyl", ["Load", "Load (from RPM and MAF)"]),
        new(2333, 0x000B, "MAPSensor", "2-Bar MAP Sensor", "kPa", ["Manifold Pressure", "Manifold Absolute Pressure"]),
        new(2334, 0x000B, "MAPSensor", "3-Bar MAP Sensor", "kPa", ["Manifold Pressure", "Manifold Absolute Pressure"]),
        new(2510, 0x1105, "LoopStatus", "Fuel System Loop Status", "Boolean", ["Loop Status", "Fuel System Status"]),
        new(2511, 0x1190, "FuelTrimCell", "Fuel Trim Cell", "Index", []),
        new(2512, 0x1105, "FuelTrimLearn", "Fuel Trim Learn", "Boolean", []),
        new(2513, 0x1922, "PowerEnrichment", "Power Enrichment", "Boolean", []),
        new(2514, 0x1105, "ColdStartup", "Cat Overtemp / Cold Startup", "Boolean", ["Cold Startup"]),
        new(2517, 0x1105, "Decel", "DFCO Active", "Boolean", ["Decel", "DFCO"]),
        new(2519, 0x139C, "EthanolContentSensorFrequency", "Flex Fuel Sensor Frequency", "Hz", ["Ethanol Content Sensor Frequency"]),
        new(2521, 0x12C5, "FuelLevel", "Fuel Level", "%", []),
        new(2522, 0x119E, "TargetAFRatio", "Commanded Air-Fuel Ratio", "AFR", ["Target A/F Ratio", "Commanded AFR"]),
        new(2630, 0x11A6, "KnockRetardDegrees", "Knock Retard", "°", ["Knock Retard Degrees"]),
        new(2636, 0x11A6, "KnockRetardDegrees", "Max Knock Retard", "°", ["Knock Retard", "Knock Retard Degrees"]),
        new(2641, 0x125E, "KnockCount", "Knock Count", "Raw", ["Spark Octane Scalar"]),
        new(2701, 0x1201, "MisfireCurrentCyl1", "Current Misfires", "Raw", ["Total Misfire Fail"]),
        new(2705, 0x1227, "TotalMisfireFail", "History Misfires", "Raw", ["Misfire History Cyl 1"]),
        new(4100, 0x1940, "TransmissinonFluidTemperature", "Transmission Fluid Temp", "C", ["Transmission Oil Temperature"]),
        new(4120, 0x199A, "CurrentGear", "Current Gear", "Boolean", ["Transmission Gear", "Current Gear (Manual)"]),
        new(4353, 0x1250, "MAFSensorFrequency", "MAF Frequency", "Hz", ["MAF Sensor Frequency"]),
        new(6001, 0x119E, "TargetAFRatio", "Commanded AFR", "AFR", ["Target A/F Ratio"]),
        new(6010, 0xFC05, "TargetEquivalenceRatio", "Commanded Equivalence Ratio", "EQR", ["Target Equivalence Ratio"]),
        new(6020, 0x1190, "FuelTrimCell", "Fuel Trim Cell", "Index", []),
        new(6100, 0x1145, "O2LeftFront", "O2 Sensor B1", "mV", ["O2 Left Front"]),
        new(6102, 0x1148, "O2RightFront", "O2 Sensor B2", "mV", ["O2 Right Front"]),
        new(6200, 0x125A, "InjectorPWMLeftBankAverage", "Injector Pulse Width B1", "ms", ["Injector PWM Left Bank Average"]),
        new(6201, 0x125B, "InjectorPWMRightBankAverage", "Injector Pulse Width B2", "ms", ["Injector PWM Right Bank Average"]),
        new(6255, null, "RamLoad", "Cylinder Air", "g/cyl", ["Load", "Load (from RPM and MAF)"]),
        new(6305, 0x0007, "LongTermFTLeftBank", "Long-Term Fuel Trim B1", "%", ["Long Term FT Left Bank"]),
        new(7101, 0x1144, "ACHighSidePressureSensor", "A/C Pressure Sensor Input", "Volts", ["A/C High Side Pressure Sensor"]),
        new(12130, 0x1106, "FCRelay1Command", "Cooling Fan 1 Command", "Boolean", ["FC Relay 1 Command"]),
        new(12131, 0x1106, "FCRelay2and3Command", "Cooling Fan 2/3 Command", "Boolean", ["FC Relay 2 and 3 Command"]),
        new(30001, 0x1144, "ACWidebandAFRatio", "Wideband AFR", ":1", ["A/C Wideband A/F Ratio", "A/C High Side Pressure Sensor"]),
        new(40143, 0x1144, "ACWidebandAFRatio", "Wideband AFR", ":1", ["Wideband AFR", "A/C Wideband A/F Ratio"]),
        // VCM Generic Sensor IDs (50xxx)
        new(50010, 0x0005, "ECTSensor", "Coolant Temperature", "°F", ["ECT Sensor", "ECT"]),
        new(50011, 0x000F, "IATSensor", "Intake Air Temperature", "°F", ["IAT Sensor", "IAT"]),
        new(50020, 0x000D, "VehicleSpeedSensor", "Vehicle Speed", "mph", ["Vehicle Speed Sensor", "VSS"]),
        new(50030, 0x000B, "MAPSensor", "Manifold Pressure", "kPa", ["MAP Sensor", "Manifold Absolute Pressure", "MAP"]),
        new(50034, 0x114E, "FuelTankPressureSensorVoltage", "Fuel Pressure", "Volts", []),
        new(50040, 0x0010, "MAFSensor", "MAF Airflow", "g/s", ["MAF Sensor", "MAF"]),
        new(50041, null, "RamLoad", "Cylinder Air", "g/cyl", ["Load", "Load (from RPM and MAF)"]),
        new(50050, 0x1141, "Ignition1Signal", "Module Voltage", "Volts", ["Ignition 1 Signal", "Control Module Voltage"]),
        new(50051, 0x1145, "O2LeftFront", "O2 Sensor B1", "mV", ["O2 Left Front"]),
        new(50070, 0x000C, "EngineSpeed", "Engine RPM", "RPM", ["Engine Speed", "RPM"]),
        new(50072, 0x199A, "CurrentGear", "Current Gear", "Boolean", ["Transmission Gear"]),
        new(50080, 0x1250, "MAFSensorFrequency", "MAF Frequency", "Hz", ["MAF Sensor Frequency"]),
        new(50090, 0x0011, "ThrottlePosition", "Throttle Position", "%", ["TP Sensor", "TPS"]),
        new(50091, 0x12B0, "AcceleratorPedalPosition", "Accelerator Pedal Position", "%", ["Throttle Position", "TPS"]),
        new(50108, 0x1337, "TorqueDeliveredSignal", "Delivered Engine Torque", "N-m", ["Torque Delivered Signal"]),
        new(50110, 0x000E, "IgnitionTiming", "Spark Advance", "°", ["Ignition Timing", "Spark"]),
        new(50111, 0x11A6, "KnockRetardDegrees", "Knock Retard", "°", ["Knock Retard Degrees"]),
        new(50114, 0x0007, "LongTermFTLeftBank", "Long-Term Fuel Trim B1", "%", ["Long Term FT Left Bank", "LTFT B1"]),
        new(50116, 0x0006, "ShortTermFTLeftBank", "Short-Term Fuel Trim B1", "%", ["Short Term FT Left Bank", "STFT B1"]),
        new(50118, 0xFC05, "TargetEquivalenceRatio", "Commanded Equivalence Ratio", "EQR", ["Target Equivalence Ratio", "Commanded AFR"]),
        new(50119, 0x1144, "ACWidebandAFRatio", "Wideband Equivalence Ratio", ":1", ["Wideband AFR", "A/C Wideband A/F Ratio"]),
        new(50120, 0x1144, "ACWidebandAFRatio", "Wideband AFR", ":1", ["Wideband AFR", "A/C Wideband A/F Ratio"]),
        new(50121, 0x119E, "TargetAFRatio", "Commanded AFR", "AFR", ["Target A/F Ratio"]),
        new(50127, 0x1144, "ACWidebandAFRatio", "Wideband EQ 1", ":1", ["Wideband AFR", "A/C Wideband A/F Ratio"]),
        new(50150, null, "MathIdcBank1", "Injector Duty Cycle", "%", ["IDC Bank 1"]),
        new(50151, 0x125A, "InjectorPWMLeftBankAverage", "Injector Pulse Width B1", "ms", ["Injector PWM Left Bank Average"]),
        new(50152, 0x125B, "InjectorPWMRightBankAverage", "Injector Pulse Width B2", "ms", ["Injector PWM Right Bank Average"]),
        new(50153, 0x1145, "O2LeftFront", "O2 Sensor B1", "mV", ["O2 Left Front"]),
        new(50154, 0x1148, "O2RightFront", "O2 Sensor B2", "mV", ["O2 Right Front"]),
        new(50155, 0x0007, "LongTermFTLeftBank", "Long-Term Fuel Trim B1", "%", ["Long Term FT Left Bank"]),
        new(50156, 0x0006, "ShortTermFTLeftBank", "Short-Term Fuel Trim B1", "%", ["Short Term FT Left Bank"]),
        new(50157, 0x0009, "LongTermFTRightBank", "Long-Term Fuel Trim B2", "%", ["Long Term FT Right Bank"]),
        new(50158, 0x0008, "ShortTermFTRightBank", "Short-Term Fuel Trim B2", "%", ["Short Term FT Right Bank"]),
        // VCM Math Parameter IDs (60xxx / 61xxx)
        new(60000, null, "MathIdcBank1", "IDC Bank 1", "%", ["Injector Duty Cycle", "INJ Duty", "Inj DC"]),
        new(60020, null, "MathAfrError", "AFR Error %", "%", ["AFR Error", "AFR % Er", "maf Err"]),
        new(60040, null, "MathCombinedFuelTrim", "Combined Fuel Trim", "%", ["LTFT + STFT", "ST+LT"]),
        new(60300, null, "MathBoostPsi", "Boost Pressure", "psi", ["Boost", "PSI"]),
        new(61000, null, "MathWheelHp", "Estimated Wheel HP", "HP", ["horsepower", "Calculated HP"]),
        new(61001, null, "MathCombinedFuelTrim", "Combined Fuel Trim", "%", ["ST+LT", "IDLE TB ADJ"]),
        new(61002, null, "MathAfrError", "AFR Error %", "%", ["AFR % Er", "AFR Error"]),
        new(61005, null, "MathBoostPsi", "Boost Pressure", "psi", ["PSI", "Boost"])
    ];

    private static readonly Dictionary<int, VcmParameterMap> ByVcmId =
        ParameterMaps.GroupBy(m => m.VcmId).ToDictionary(g => g.Key, g => g.First());

    public static string UnitSymbol(int unitId) => unitId switch
    {
        10 => "V",
        14 => "mV",
        50 => "Hz",
        56 => "RPM",
        71 => "lb/min",
        73 => "g/s",
        91 => "kPa",
        98 => "psi",
        114 => "mph",
        115 => "kph",
        127 => "N-m",
        150 => "",
        155 or 156 or 196 => "%",
        161 => "°",
        223 or 224 => "g/cyl",
        234 or 238 => "EQR",
        241 => "°C",
        242 => "°F",
        254 => "ms",
        255 => "Hz",
        _ => ""
    };

    public static bool UnitsCompatible(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return true;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;
        static string Canon(string u) => u.Trim().ToLowerInvariant() switch
        {
            "v" or "volt" or "volts" => "v",
            "deg" or "degrees" or "°" => "deg",
            "g/s" or "g/sec" or "lb/min" => "airflow",
            "eqr" or "lambda" or "afr" or ":1" => "afr",
            "c" or "°c" or "f" or "°f" => "temp",
            "kpa" or "psi" or "bar" => "pressure",
            _ => u.Trim().ToLowerInvariant()
        };
        return Canon(a) == Canon(b);
    }

    public static bool MatchesChannel(Channel channel, int vcmId)
    {
        if (channel.Pid == vcmId)
            return true;
        if (!ByVcmId.TryGetValue(vcmId, out var map))
            return false;
        // Some enhanced PIDs carry several signals in separate bits or use multiple conversions.
        if (map.ObdPid.HasValue && channel.Pid == map.ObdPid.Value &&
            ParameterMaps.Where(candidate => candidate.ObdPid == map.ObdPid)
                .All(candidate => candidate.CatalogId == map.CatalogId))
            return true;
        if (channel.Name.Equals(map.Name, StringComparison.OrdinalIgnoreCase))
            return true;
        return map.Aliases.Any(alias => channel.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryResolveMathName(string token, out string name)
    {
        string cleaned = token.Split('.', 2)[0].Trim();
        if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
            ByVcmId.TryGetValue(id, out var map))
        {
            name = map.Name;
            return true;
        }
        name = "";
        return false;
    }

    public static int? TryGetVcmMathId(string mathName)
    {
        var match = ParameterMaps.FirstOrDefault(m => m.VcmId >= 50150 &&
            (m.Name.Equals(mathName, StringComparison.OrdinalIgnoreCase) ||
             m.Aliases.Any(a => a.Equals(mathName, StringComparison.OrdinalIgnoreCase))));
        return match?.VcmId;
    }

    public static bool SameUnit(string source, string target)
    {
        static string Normalize(string unit) => unit.Trim().ToLowerInvariant() switch
        {
            "volt" or "volts" => "v",
            "deg" or "degrees" => "°",
            "c" => "°c",
            "f" => "°f",
            "km/h" => "kph",
            "g/sec" => "g/s",
            _ => unit.Trim().ToLowerInvariant()
        };
        return Normalize(source) == Normalize(target);
    }

    public static ChannelSource ResolveSource(int vcmId, int? unitCode, ScanLog? log)
    {
        string unit = unitCode is int code ? UnitSymbol(code) : "";
        string name = ByVcmId.TryGetValue(vcmId, out var mapping) ? mapping.Name : $"PID {vcmId}";
        var source = new ChannelSource(name, unit, VcmId: vcmId);
        return source.Resolve(log) is { } channel && log is not null ? ChannelSource.From(log, channel) : source;
    }

    public static LiveChannelSetting[] ImportChannelsXml(string path, uint os = LiveScanner.DefaultOs)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 512 * 1024)
            throw new InvalidDataException("The VCM Channels XML exceeds 512 KB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        return ParseChannelsElement(root, os);
    }

    public static LiveChannelSetting[] ParseChannelsElement(XElement root, uint os = LiveScanner.DefaultOs)
    {
        var catalog = LiveScanner.Catalog(os);
        var byId = catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var byPid = catalog.Where(c => c.Pid.HasValue).GroupBy(c => (int)c.Pid!.Value).ToDictionary(g => g.Key, g => g.First());

        var selected = new List<LiveChannelSetting>();
        int usedBytes = 0;

        foreach (var el in root.Descendants().Where(e => e.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase)))
        {
            string? pidAttr = (string?)el.Attribute("ParameterID") ?? (string?)el.Attribute("parameterId");
            if (!int.TryParse(pidAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vcmId) || vcmId <= 0)
                continue;

            LiveChannelChoice? choice = null;
            string preferredUnit = "";
            if (ByVcmId.TryGetValue(vcmId, out var map))
            {
                preferredUnit = map.DefaultUnit;
                if (!byId.TryGetValue(map.CatalogId, out choice) && map.ObdPid.HasValue)
                    byPid.TryGetValue(map.ObdPid.Value, out choice);
            }
            if (choice is null)
                byPid.TryGetValue(vcmId, out choice);
            if (choice is null || selected.Any(s => s.Id == choice.Id))
                continue;
            if (usedBytes + choice.Bytes > LiveScanner.MaximumLiveBytes)
                continue;

            string unit = choice.Units.FirstOrDefault(u => u.Equals(preferredUnit, StringComparison.OrdinalIgnoreCase))
                ?? choice.Units.FirstOrDefault(u => UnitsCompatible(u, preferredUnit))
                ?? choice.Units[0];
            selected.Add(new LiveChannelSetting(choice.Id, unit, choice.Name));
            usedBytes += choice.Bytes;
        }

        if (selected.Count == 0)
            throw new InvalidDataException("No supported P01/P59 channels matched the VCM Channels XML.");
        return selected.ToArray();
    }

    public static GraphSetting[] ImportGraphsXml(string path, ScanLog? log = null)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 512 * 1024)
            throw new InvalidDataException("The VCM Graphs XML exceeds 512 KB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);
        return ParseGraphTables(root, log);
    }

    public static GraphSetting[] ParseGraphTables(XElement root, ScanLog? log = null)
    {
        var tables = new List<GraphSetting>();
        foreach (var table in root.Descendants().Where(e => e.Name.LocalName.Equals("Table", StringComparison.OrdinalIgnoreCase)))
        {
            string label = ((string?)table.Element("Label") ?? $"Graph {tables.Count + 1}").Trim();
            int valId = ParseInt(table.Element("ParameterID"), 50110);
            int? valUnit = ParseNullableInt(table.Element("Unit"));
            int colId = ParseInt(table.Element("ColumnParameterID"), 50070);
            int? colUnit = ParseNullableInt(table.Element("ColumnUnit"));
            int rowId = ParseInt(table.Element("RowParameterID"), colId == 50070 ? 50030 : 50070);
            int? rowUnit = ParseNullableInt(table.Element("RowUnit"));
            if (colId == rowId)
                rowId = colId == 50070 ? 50030 : 50070;

            double[] colBreaks = ParseBreakpoints((string?)table.Element("ColumnAxis"));
            double[] rowBreaks = ParseBreakpoints((string?)table.Element("RowAxis"));
            double colWidth = DefaultStep(colId, colBreaks);
            double rowWidth = DefaultStep(rowId, rowBreaks);
            int hits = Math.Clamp(ParseInt(table.Element("CellHitsRequired"), 1), 1, 1_000_000);
            int valueType = ParseInt(table.Element("GraphValueType"), 0);
            GraphStatistic stat = valueType switch
            {
                1 => GraphStatistic.Maximum,
                2 => GraphStatistic.Minimum,
                3 => GraphStatistic.Count,
                _ => GraphStatistic.Mean
            };
            string? filter = ((string?)table.Element("FilterFunction"))?.Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                try { _ = new MathFormula(filter); }
                catch (FormatException) { filter = null; }
            }

            var xAxis = new GraphAxis(ResolveSource(colId, colUnit, log), colWidth, colBreaks);
            var yAxis = new GraphAxis(ResolveSource(rowId, rowUnit, log), rowWidth, rowBreaks);
            var valSource = ResolveSource(valId, valUnit, log);
            tables.Add(new GraphSetting(xAxis, yAxis, valSource, stat, hits, label,
                string.IsNullOrEmpty(filter) ? null : filter));
        }
        return tables.ToArray();
    }

    public static ScannerLayout ImportLayoutXml(string path, ScannerLayout current, ScanLog? log = null, uint os = LiveScanner.DefaultOs)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
            throw new InvalidDataException("The VCM Layout XML exceeds 1 MB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XElement.Load(reader);

        if (root.Name.LocalName.Equals("Channels", StringComparison.OrdinalIgnoreCase))
            return current with { LiveChannels = ParseChannelsElement(root, os) };

        if (root.Name.LocalName.Equals("Graphs", StringComparison.OrdinalIgnoreCase))
        {
            var importedGraphs = ParseGraphTables(root, log);
            if (importedGraphs.Length == 0)
                throw new InvalidDataException("No <Table> entries found in the VCM Graphs XML.");
            return current with { Graph = importedGraphs[0], Graphs = importedGraphs };
        }

        if (root.Name.LocalName.Equals("MathParameter", StringComparison.OrdinalIgnoreCase) ||
            root.Name.LocalName.Equals("MathParameters", StringComparison.OrdinalIgnoreCase))
        {
            var importedMaths = MathSetting.LoadVcmXml(path);
            var merged = MergeMaths(current.Maths ?? MathSetting.Defaults, importedMaths);
            return current with { Maths = merged };
        }

        var gauges = new List<GaugeSetting>();
        int colorIdx = 0;
        foreach (var gauge in root.Descendants().Where(e => e.Name.LocalName.Equals("Gauge", StringComparison.OrdinalIgnoreCase)))
        {
            if (gauges.Count >= 12)
                break;
            int pid = ParseInt(gauge.Element("ParameterID"), 50070);
            int? unit = ParseNullableInt(gauge.Element("Unit"));
            string label = ((string?)gauge.Element("Label") ?? $"PID {pid}").Trim();
            int type = ParseInt(gauge.Element("Type"), 1);
            GaugeKind kind = type switch
            {
                2 => GaugeKind.Bar,
                3 => GaugeKind.Digital,
                _ => GaugeKind.Dial
            };
            double min = ParseDouble(gauge.Element("Minimum"), 0);
            double max = ParseDouble(gauge.Element("Maximum"), 100);
            if (max <= min)
                max = min + 100;
            var source = ResolveSource(pid, unit, log);
            gauges.Add(new GaugeSetting(source, label.Length == 0 ? source.ToString() : label, kind, min, max, colorIdx++ % 6));
        }

        var traces = new List<TraceSetting>();
        int traceColor = 0;
        foreach (var series in root.Descendants().Where(e => e.Name.LocalName.Equals("Series", StringComparison.OrdinalIgnoreCase)))
        {
            if (traces.Count >= ScannerLayout.MaximumTraces)
                break;
            int pid = ParseInt(series.Element("ParameterID"), 50070);
            int? unit = ParseNullableInt(series.Element("Unit"));
            var source = ResolveSource(pid, unit, log);
            if (traces.Any(t => t.Source == source))
                continue;
            double? min = series.Element("Minimum") is { } minEl ? ParseDouble(minEl, 0) : null;
            double? max = series.Element("Maximum") is { } maxEl ? ParseDouble(maxEl, 100) : null;
            if (min.HasValue && !max.HasValue)
                max = min.Value + 100;
            else if (!min.HasValue && max.HasValue)
                min = 0;
            if (min.HasValue && max.HasValue && max.Value <= min.Value)
                (min, max) = (null, null);
            traces.Add(new TraceSetting(source, traceColor++ % ScannerLayout.ColorCount, min, max));
        }

        var graphs = ParseGraphTables(root, log);
        var nextMaths = MergeMaths(current.Maths ?? MathSetting.Defaults, MathSetting.VcmTelemetryBuiltIns);

        return current with
        {
            Gauges = gauges.Count > 0 ? gauges.ToArray() : current.Gauges,
            Traces = traces.Count > 0 ? traces.ToArray() : current.Traces,
            Graph = graphs.Length > 0 ? graphs[0] : current.Graph,
            Graphs = graphs.Length > 0 ? graphs : current.Graphs,
            Maths = nextMaths
        };
    }

    public static MathSetting[] MergeMaths(MathSetting[] existing, MathSetting[] additions)
    {
        var list = new List<MathSetting>(existing);
        foreach (var item in additions)
        {
            int idx = list.FindIndex(m => m.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                list[idx] = item with { Id = list[idx].Id ?? item.Id };
            else if (list.Count < MathSetting.Maximum)
                list.Add(item);
        }
        return list.ToArray();
    }

    private static int ParseInt(XElement? el, int fallback) =>
        el is not null && int.TryParse(el.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static int? ParseNullableInt(XElement? el) =>
        el is not null && int.TryParse(el.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static double ParseDouble(XElement? el, double fallback) =>
        el is not null && double.TryParse(el.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : fallback;

    private static double[] ParseBreakpoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var values = new List<double>();
        foreach (string token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                values.Add(v);
        }
        var sorted = values.Distinct().Order().ToArray();
        return sorted.Length <= 120 ? sorted : sorted.Take(120).ToArray();
    }

    private static double DefaultStep(int vcmId, double[] breaks)
    {
        if (breaks.Length >= 2)
        {
            double diff = breaks.Zip(breaks.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(1).Min();
            return Math.Max(0.01, diff);
        }
        return vcmId switch
        {
            12 or 50070 => 400,
            11 or 50030 => 10,
            2301 or 50080 or 4353 => 150,
            67 or 50041 or 2154 => 0.08,
            _ => 10
        };
    }
}
