// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using PcmHacking;

namespace WrongWay;

enum VcmControlAction
{
    CrankRelearn,
    ClearFuelTrims,
    ClearTroubleCodes,
    Fan1On,
    Fan1Off,
    Fan2On,
    Fan2Off,
    FuelPumpOn,
    FuelPumpOff,
    ForceOpenLoop,
    ForceClosedLoop,
    CommandIdleRpm,
    CommandAfr,
    CommandSparkAdvance,
    CylinderPowerBalance,
    ReleaseAllControls,
    ReleaseFuelPumpControl
}

sealed record VcmControlRequest(
    VcmControlAction Action,
    double? TargetValue = null,
    int? CylinderIndex = null);

sealed record TelemetrySessionRecord(
    string Id,
    string Name,
    string Path,
    DateTimeOffset RecordedUtc,
    uint? OsId,
    int Frames,
    double DurationSeconds,
    string[] Channels,
    string? Vin = null,
    TelemetryPeaks? Peaks = null);

sealed record TelemetryValue(double Value, string Unit);

sealed record TelemetryPeaks(TelemetryValue? Rpm, TelemetryValue? Speed, TelemetryValue? Coolant,
    TelemetryValue? Map, TelemetryValue? KnockRetard, TelemetryValue? MinAfr, TelemetryValue? MaxAfr);

static class VcmTelemetryControls
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string SessionsFilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "wrongway-suite", "telemetry-sessions.json");

    public static bool AvailableLive(VcmControlAction action) => action is
        VcmControlAction.Fan1On or VcmControlAction.Fan1Off or VcmControlAction.Fan2On or
        VcmControlAction.Fan2Off or VcmControlAction.ReleaseAllControls or VcmControlAction.ClearTroubleCodes or
        VcmControlAction.FuelPumpOn or VcmControlAction.FuelPumpOff or VcmControlAction.ReleaseFuelPumpControl;

    /// <summary>
    /// Builds the frame for a named control. Only AvailableLive controls can be sent by the scanner.
    /// </summary>
    public static byte[] BuildVpwCommand(VcmControlRequest request)
    {
        return request.Action switch
        {
            // CASE preview from a captured GM LS1 session. The live relearn procedure needs PID monitoring.
            VcmControlAction.CrankRelearn =>
                [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x01, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00],

            // Clear Fuel Trims / Reset Learned Adaptives: Mode $AE CPID $01, Byte 4 = 0x40 (Fuel Trim Reset bit)
            VcmControlAction.ClearFuelTrims =>
                [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x01, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00],

            // Clear DTCs (Functional broadcast Mode $04)
            VcmControlAction.ClearTroubleCodes =>
                [Priority.Functional0, 0x6A, DeviceId.Tool, Mode.ClearDiagnosticTroubleCodes],

            VcmControlAction.Fan1On => BuildFanCommand(0x80, 0x80),
            VcmControlAction.Fan1Off => BuildFanCommand(0x80, 0x00),
            VcmControlAction.Fan2On => BuildFanCommand(0x40, 0x40),
            VcmControlAction.Fan2Off => BuildFanCommand(0x40, 0x00),

            // P59 CPID $01: bit 2 in the third/fourth data bytes enables/sets the pump relay.
            // 12587603 loc_3D12E; 12587811 0x415B2. See Reference/P59-Fuel-Pump.md.
            VcmControlAction.FuelPumpOn => BuildFanCommand(0x04, 0x04),
            VcmControlAction.FuelPumpOff => BuildFanCommand(0x04, 0x00),
            // CPID $00 clears all override groups and resets the pump-test timer state.
            VcmControlAction.ReleaseFuelPumpControl =>
                [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x00, 0, 0, 0, 0, 0, 0],

            // Open/Closed Loop Fueling Status Override (Mode $AE CPID $04)
            VcmControlAction.ForceOpenLoop =>
                [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00],
            VcmControlAction.ForceClosedLoop =>
                [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x04, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00],

            // Target Idle RPM Override (Mode $AE CPID $03: raw = RPM / 12.5, clamped 400..2500 RPM)
            VcmControlAction.CommandIdleRpm => BuildIdleRpmCommand(request.TargetValue ?? 800),

            // Commanded Air-Fuel Ratio Override (Mode $AE CPID $04: raw = AFR * 10, clamped 10.0..18.0 AFR)
            VcmControlAction.CommandAfr => BuildAfrCommand(request.TargetValue ?? 14.7),

            // Spark Advance Override (Mode $AE CPID $05: raw = (deg + 64) * 2, clamped -10..45 deg)
            VcmControlAction.CommandSparkAdvance => BuildSparkCommand(request.TargetValue ?? 20.0),

            // Cylinder Power Balance / Injector Cutout (Mode $AE CPID $06: cylinder bitmask 1..8, 0 = enable all)
            VcmControlAction.CylinderPowerBalance => BuildCylinderBalanceCommand(request.CylinderIndex ?? 0),

            // Clear the CPID $01 override masks to return the fan relays to PCM control.
            VcmControlAction.ReleaseAllControls => BuildFanCommand(0x00, 0x00),

            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    // CPID $01 uses separate enable and state bytes for both fan relays.
    private static byte[] BuildFanCommand(byte enable, byte state) =>
        [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x01, 0x00, 0x00, enable, state, 0x00, 0x00];

    private static byte[] BuildIdleRpmCommand(double rpm)
    {
        if (!double.IsFinite(rpm) || rpm is < 400 or > 2500)
            throw new FormatException("Enter a target idle speed between 400 and 2500 RPM.");
        byte encoded = (byte)Math.Clamp((int)Math.Round(rpm / 12.5), 32, 200);
        return [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x03, 0x80, encoded, 0x00, 0x00, 0x00, 0x00];
    }

    private static byte[] BuildAfrCommand(double afr)
    {
        if (!double.IsFinite(afr) || afr is < 10.0 or > 18.0)
            throw new FormatException("Enter a commanded AFR between 10.0 and 18.0.");
        byte encoded = (byte)Math.Clamp((int)Math.Round(afr * 10.0), 100, 180);
        return [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x04, 0x80, encoded, 0x00, 0x00, 0x00, 0x00];
    }

    private static byte[] BuildSparkCommand(double degrees)
    {
        if (!double.IsFinite(degrees) || degrees is < -10.0 or > 45.0)
            throw new FormatException("Enter a spark advance between -10.0° and 45.0°.");
        byte encoded = (byte)Math.Clamp((int)Math.Round((degrees + 64.0) * 2.0), 108, 218);
        return [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x05, 0x80, encoded, 0x00, 0x00, 0x00, 0x00];
    }

    private static byte[] BuildCylinderBalanceCommand(int cylinder)
    {
        if (cylinder is < 0 or > 8)
            throw new FormatException("Choose cylinder 1–8 to disable, or 0 to enable all cylinders.");
        byte mask = cylinder == 0 ? (byte)0xFF : (byte)~(1 << (cylinder - 1));
        return [Priority.Physical0, DeviceId.Pcm, DeviceId.Tool, Mode.SpecialFunctions, 0x06, mask, 0x00, 0x00, 0x00, 0x00, 0x00];
    }

    public static TelemetrySessionRecord[] LoadSessions()
    {
        string path = SessionsFilePath;
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<TelemetrySessionRecord[]>(stream, Json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static TelemetrySessionRecord RegisterSession(ScanLog log, string filePath, uint? osId = null)
    {
        var existing = LoadSessions().ToList();
        string fullPath = Path.GetFullPath(filePath);
        var previous = existing.FirstOrDefault(session => session.Path.Equals(fullPath, StringComparison.Ordinal));
        var record = new TelemetrySessionRecord(
            Id: previous?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Name: log.Name,
            Path: fullPath,
            RecordedUtc: File.GetLastWriteTimeUtc(fullPath),
            OsId: osId ?? previous?.OsId,
            Frames: log.Frames,
            DurationSeconds: Math.Round(log.Duration / 1000.0, 3),
            Channels: log.Channels.Select(c => c.DisplayLabel).Take(64).ToArray(),
            Vin: ReadVin(log),
            Peaks: GetPeaks(log));
        existing.RemoveAll(s => s.Path.Equals(fullPath, StringComparison.Ordinal));
        existing.Insert(0, record);
        if (existing.Count > 100)
            existing.RemoveRange(100, existing.Count - 100);
        SaveSessions(existing.ToArray());
        return record;
    }

    public static void SaveSessions(TelemetrySessionRecord[] sessions)
    {
        string path = SessionsFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(sessions, Json));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static void ExportTelemetryBundle(ScanLog log, HistogramSnapshot? histogram, uint? osId, string destinationPath)
    {
        var payload = new
        {
            Schema = "WrongWay.VcmTelemetrySession.v1",
            ExportedUtc = DateTimeOffset.UtcNow,
            LogName = log.Name,
            ScannerVersion = log.ScannerVersion ?? typeof(Program).Assembly.GetName().Version?.ToString(3),
            OsId = osId,
            Vin = ReadVin(log),
            Peaks = GetPeaks(log),
            log.Frames,
            DurationSeconds = Math.Round(log.Duration / 1000.0, 3),
            Channels = log.Channels.Select(c => new
            {
                c.Key,
                c.Pid,
                c.IsMath,
                Name = c.DisplayName,
                Unit = c.DisplayUnit,
                SampleCount = c.Readings.Count,
                Samples = c.Readings.Where(r => r.Number.HasValue || r.Text is not null).Select(r => new
                {
                    TimeMs = r.Time - log.Start,
                    Value = c.DisplayNumber(r),
                    r.Text
                })
            }),
            Histogram = histogram is null ? null : new
            {
                histogram.XLabel,
                histogram.YLabel,
                histogram.ValueLabel,
                Statistic = histogram.Statistic.ToString(),
                histogram.MinimumSamples,
                Cells = histogram.Cells.Select(kv => new
                {
                    X = kv.Key.X,
                    Y = kv.Key.Y,
                    kv.Value.Mean,
                    kv.Value.Minimum,
                    kv.Value.Maximum,
                    kv.Value.Count
                })
            }
        };
        string full = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, payload, Json);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string? ReadVin(ScanLog log) =>
        log.Channels.Where(channel => channel.Name.Equals("VIN", StringComparison.OrdinalIgnoreCase))
            .SelectMany(channel => channel.Readings)
            .Select(reading => reading.Text?.Trim().ToUpperInvariant())
            .FirstOrDefault(vin => vin is { Length: 17 } &&
                vin.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' && c is not ('I' or 'O' or 'Q')));

    private static TelemetryPeaks GetPeaks(ScanLog log)
    {
        static TelemetryValue? Extreme(Channel? channel, bool minimum = false)
        {
            if (channel is null)
                return null;
            var values = channel.Readings.Select(channel.DisplayNumber)
                .Where(value => value.HasValue && double.IsFinite(value.Value));
            double? extreme = minimum ? values.Min() : values.Max();
            return extreme.HasValue ? new TelemetryValue(extreme.Value, channel.DisplayUnit) : null;
        }

        var rpm = MathChannel.Resolve(log.Channels, "50070");
        var speed = MathChannel.Resolve(log.Channels, "50020");
        var coolant = MathChannel.Resolve(log.Channels, "50010");
        var map = MathChannel.Resolve(log.Channels, "50030");
        var knock = MathChannel.Resolve(log.Channels, "50111");
        var afr = log.Channels.FirstOrDefault(channel => channel.Name.Equals("Wideband AFR", StringComparison.OrdinalIgnoreCase)
            && channel.Unit.ToUpperInvariant() is "AFR" or ":1");
        return new TelemetryPeaks(Extreme(rpm), Extreme(speed), Extreme(coolant), Extreme(map),
            Extreme(knock), Extreme(afr, minimum: true), Extreme(afr));
    }

    public static void SelfTest()
    {
        if (!BuildVpwCommand(new(VcmControlAction.FuelPumpOn)).SequenceEqual(Convert.FromHexString("6C10F0AE01000004040000"))
            || !BuildVpwCommand(new(VcmControlAction.FuelPumpOff)).SequenceEqual(Convert.FromHexString("6C10F0AE01000004000000"))
            || !BuildVpwCommand(new(VcmControlAction.ReleaseFuelPumpControl)).SequenceEqual(Convert.FromHexString("6C10F0AE00000000000000"))
            || !AvailableLive(VcmControlAction.FuelPumpOn) || !AvailableLive(VcmControlAction.FuelPumpOff))
            throw new InvalidDataException("P59 pump control and release frame mismatch.");
        byte[] caseFrame = BuildVpwCommand(new VcmControlRequest(VcmControlAction.CrankRelearn));
        if (!caseFrame.SequenceEqual(new byte[] { 0x6C, 0x10, 0xF0, 0xAE, 0x01, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00 }))
            throw new InvalidDataException("CASE Crank Relearn VPW frame mismatch.");

        byte[] idleFrame = BuildVpwCommand(new VcmControlRequest(VcmControlAction.CommandIdleRpm, 850));
        if (idleFrame[3] != 0xAE || idleFrame[4] != 0x03 || idleFrame[6] != 68)
            throw new InvalidDataException("Command Idle RPM VPW frame mismatch.");

        byte[] afrFrame = BuildVpwCommand(new VcmControlRequest(VcmControlAction.CommandAfr, 12.8));
        if (afrFrame[3] != 0xAE || afrFrame[4] != 0x04 || afrFrame[6] != 128)
            throw new InvalidDataException("Command AFR VPW frame mismatch.");

        byte[] sparkFrame = BuildVpwCommand(new VcmControlRequest(VcmControlAction.CommandSparkAdvance, 24.0));
        if (sparkFrame[3] != 0xAE || sparkFrame[4] != 0x05 || sparkFrame[6] != 176)
            throw new InvalidDataException("Command Spark Advance VPW frame mismatch.");

        byte[] cylFrame = BuildVpwCommand(new VcmControlRequest(VcmControlAction.CylinderPowerBalance, CylinderIndex: 3));
        if (cylFrame[3] != 0xAE || cylFrame[4] != 0x06 || cylFrame[5] != 0xFB)
            throw new InvalidDataException("Cylinder Power Balance VPW frame mismatch.");

        string tempXmlDir = Path.Combine(Path.GetTempPath(), "wrongway-vcm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempXmlDir);
        try
        {
            string mathXml = Path.Combine(tempXmlDir, "CL.MathParameter.xml");
            File.WriteAllText(mathXml,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<MathParameter Notes=\"\" Decimals=\"0\" Unit=\"150\" Expression=\"[2517.161.avg(1500)]=0 and [2517.161.avg(-200)]=0 and (abs([50090.156.slope(1500)])+abs([50090.156.slope(-500)]))&lt;2 and [50118.238] = 1.00\" Abbreviation=\"\" Name=\"CL\" />");
            var loadedMaths = MathSetting.LoadVcmXml(mathXml);
            if (loadedMaths.Length != 1 || loadedMaths[0].Name != "CL")
                throw new InvalidDataException("VCM MathParameter.xml failed to import.");

            string channelsXml = Path.Combine(tempXmlDir, "test.Channels.xml");
            File.WriteAllText(channelsXml,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<Channels><channel ParameterID=\"12\" Interval=\"00:00:00.1\" /><channel ParameterID=\"11\" /><channel ParameterID=\"17\" /><channel ParameterID=\"6001\" /></Channels>");
            var importedChannels = VcmConfigImporter.ImportChannelsXml(channelsXml);
            if (importedChannels.Length < 3)
                throw new InvalidDataException("VCM Channels.xml failed to map P59 channels.");

            string layoutXml = Path.Combine(tempXmlDir, "32-traces.Layout.xml");
            File.WriteAllText(layoutXml, "<DocumentContents>" + string.Concat(
                Enumerable.Range(0, ScannerLayout.MaximumTraces).Select(i =>
                    $"<Series><ParameterID>{90000+i}</ParameterID><Unit>150</Unit></Series>")) + "</DocumentContents>");
            var expanded = VcmConfigImporter.ImportLayoutXml(layoutXml, ScannerLayout.Default);
            if (expanded.Traces?.Length != ScannerLayout.MaximumTraces
                || expanded.Traces.Select(t => t.Color).Distinct().Count() != ScannerLayout.ColorCount
                || !expanded.IsValid()
                || TraceChart.SeriesBrushes.Length != ScannerLayout.ColorCount
                || TraceChart.ColorNames.Length != ScannerLayout.ColorCount
                || TraceChart.MinimumPlotHeight(32) < 32 * 54)
                throw new InvalidDataException("32-trace VCM import, palette or scroll height failed.");

            string graphsXml = Path.Combine(tempXmlDir, "test.Graphs.xml");
            File.WriteAllText(graphsXml,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<Graphs><Table><Label>AFR Err</Label><FilterFunction>[50090.156]&gt;30%</FilterFunction><ParameterID>60020</ParameterID><Unit>156</Unit><CellHitsRequired>10</CellHitsRequired><ColumnParameterID>50030</ColumnParameterID><ColumnUnit>91</ColumnUnit><ColumnAxis>30,40,50,60,70,80,90,100</ColumnAxis><RowParameterID>50070</RowParameterID><RowUnit>56</RowUnit><RowAxis>800,1200,1600,2000,2400,2800</RowAxis></Table></Graphs>");
            var importedGraphs = VcmConfigImporter.ImportGraphsXml(graphsXml);
            if (importedGraphs.Length != 1 || importedGraphs[0].Label != "AFR Err" || importedGraphs[0].MinimumSamples != 10 || importedGraphs[0].FilterExpression != "[50090.156]>30%")
                throw new InvalidDataException("VCM Graphs.xml failed to import table or filter function.");
        }
        finally
        {
            Directory.Delete(tempXmlDir, recursive: true);
        }
    }
}
