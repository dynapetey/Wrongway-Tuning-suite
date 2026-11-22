// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using PcmHacking;

namespace WrongWay;

sealed record LiveColumn(string Id, string Name, string Unit, int? Pid);
sealed record LiveReading(long Time, LiveColumn[] Columns, Reading[] Values);

sealed record LiveChannelChoice(string Id, string Name, string[] Units, int Bytes, uint? Pid, uint? RamAddress)
{
    public string? PidHex => Pid is { } pid ? $"0x{pid:X4}" : null;
    public string AddressLabel => PidHex ?? (RamAddress is { } address ? $"RAM 0x{address:X6}" : Id);
    public override string ToString() => $"{Name}  ·  {AddressLabel}";

    public bool Matches(string search) => Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || AddressLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Pid?.ToString(CultureInfo.InvariantCulture) == search;
}

static class LiveScanner
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    public static readonly string[] Profiles = ["WrongWayP59", "Basics", "FuelTrims"];

    public const string Custom = "Custom";
    public const uint DefaultOs = 12587811;
    public const int MaximumLiveBytes = DpidConfiguration.MaxGroups * ParameterGroup.MaxBytes;

    public static LiveChannelChoice[] Catalog(uint os) =>
        Database().ListParametersBySupportedOs(os).OfType<PcmParameter>()
            .Where(parameter => parameter.ByteCount > 0 && parameter.Conversions.Any())
            .Select(parameter => new LiveChannelChoice(parameter.Id, parameter.Name,
                parameter.Conversions.Select(conversion => conversion.Units).Distinct().ToArray(), parameter.ByteCount,
                (parameter as PidParameter)?.PID,
                parameter is RamParameter ram && ram.TryGetAddress(os, out uint address) ? address : null))
            .OrderBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static LiveChannelSetting[] ProfileSettings(string name, uint os) =>
        new LogProfileReader(Database(), os, new ScannerLogger(_ => { })).Read(ProfilePath(name)).Columns
            .Where(column => column.Parameter is PcmParameter)
            .Select(column => new LiveChannelSetting(column.Parameter.Id, column.Conversion.Units, column.Parameter.Name))
            .DistinctBy(column => column.Id).ToArray();

    private static LogProfile CreateProfile(ParameterDatabase database, uint os, LiveChannelSetting[] settings)
    {
        var profile = new LogProfile();
        int bytes = 0;
        foreach (var choice in settings)
        {
            if (!database.TryGetParameter<PcmParameter>(choice.Id, out var parameter) || parameter.ByteCount == 0 || !parameter.IsSupported(os))
                throw new InvalidDataException($"{choice.Label} ({choice.Id}) is unavailable for PCM OS {os}. Edit Live Channels and select a supported channel.");
            var conversion = parameter.GetConversion(choice.Unit);
            if (conversion is null)
                throw new InvalidDataException($"The PCM catalog has no {choice.Unit} conversion for {choice.Label}.");
            bytes += parameter.ByteCount;
            if (bytes > MaximumLiveBytes)
                throw new InvalidDataException($"This profile exceeds the PCM’s {MaximumLiveBytes}-byte limit. Remove recording channels.");
            profile.AddColumn(new LogColumn(parameter, conversion, false));
        }
        return profile;
    }

    public static string[] Ports()
    {
        var paths = Directory.EnumerateFiles("/dev")
            .Where(path => path.StartsWith("/dev/ttyUSB", StringComparison.Ordinal) ||
                           path.StartsWith("/dev/ttyACM", StringComparison.Ordinal) ||
                           path.StartsWith("/dev/rfcomm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        if (!Directory.Exists("/dev/serial/by-id"))
            return paths.ToArray();
        return Directory.EnumerateFiles("/dev/serial/by-id").Order(StringComparer.Ordinal)
            .Concat(paths).ToArray();
    }

    public static int SelfTest()
    {
        var logger = new ScannerLogger(_ => { });
        var db = Database();
        int total = 0;
        foreach (string name in Profiles)
        {
            var profile = new LogProfileReader(db, 12587811, logger).Read(ProfilePath(name));
            int columns = profile.Columns.Count();
            if (columns == 0)
                throw new InvalidDataException($"Bundled profile {name} contains no channels.");
            Console.WriteLine($"{name}: {columns} channels");
            total += columns;
        }
        var sample = ProfileSettings(Profiles[0], DefaultOs);
        var catalog = Catalog(DefaultOs);
        if (!catalog.Any(channel => channel.Pid == 0x000C && channel.Matches("0x000C") && channel.Matches("12")) ||
            sample.Any(channel => !catalog.Any(choice => choice.Id == channel.Id)))
            throw new InvalidDataException("The P59 catalog is missing PID or profile definitions.");
        if (sample.Length == 0 || CreateProfile(db, DefaultOs, sample).Columns.Count() != sample.Length)
            throw new InvalidDataException("Custom live profile could not rebuild the P59 channels.");
        try
        {
            CreateProfile(db, DefaultOs, [new("invalid-pid", "rpm", "Unsupported test channel")]);
            throw new InvalidOperationException("Custom live profile accepted an unknown PID.");
        }
        catch (InvalidDataException) { }
        Console.WriteLine($"Custom profile: {sample.Length} channels and unknown PID rejected");
        return total;
    }

    public static async Task SerialTest(string portName)
    {
        using var port = new LinuxSerialPort(portName);
        await port.OpenAsync(new SerialPortConfiguration { BaudRate = 115200, Timeout = 2000 });
        await port.Send([0x12, 0x34, 0x56]);
        var response = new byte[3];
        int received = 0;
        while (received < response.Length)
        {
            int length = await port.Receive(response, received, response.Length - received);
            if (length == 0)
                throw new IOException("Serial loopback timed out.");
            received += length;
        }
        if (!response.SequenceEqual(new byte[] { 0x65, 0x43, 0x21 }))
            throw new IOException("Serial loopback returned unexpected bytes.");
    }

    public static async Task Record(
        string port, string profileName, LiveChannelSetting[]? custom, string path, Action<string> status,
        Action<uint> identify, Action<LiveReading> reading, CancellationToken token)
    {
        if (File.Exists(path))
            throw new IOException("Choose a new CSV filename so the previous log is preserved.");

        await WithVehicle(port, status, token, async (vehicle, logger) =>
        {
            var os = await vehicle.QueryOperatingSystemId(token);
            if (os.Status != ResponseStatus.Success)
                throw new IOException("The PCM did not return its operating system ID.");
            identify(os.Value);
            var database = Database();
            var profile = profileName == Custom
                ? CreateProfile(database, os.Value, custom ?? [])
                : new LogProfileReader(database, os.Value, logger).Read(ProfilePath(profileName));
            if (profile.IsEmpty)
                throw new InvalidDataException("The selected profile contains no channels for this PCM OS.");
            status($"PCM OS {os.Value} · starting {profileName}");
            using var can = new CanLogger(database, logger);
            await can.SetPort(null);
            var scanner = vehicle.CreateLogger(os.Value, can, profile.Columns, logger);
            token.ThrowIfCancellationRequested();
            try
            {
                if (!await scanner.StartLogging())
                    throw new IOException("The PCM did not start streaming live data.");
                using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
                writer.AutoFlush = false;
                long lastFlushMs = 0;
                var clock = Stopwatch.StartNew();
                LiveColumn[]? columns = null;
                int[]? order = null;
                int misses = 0;
                while (!token.IsCancellationRequested)
                {
                    var row = (await scanner.GetNextRowV2()).ToArray();
                    if (row.Length == 0)
                    {
                        if (++misses >= 5 && !token.IsCancellationRequested)
                            throw new IOException("No complete live frames after five attempts.");
                        continue;
                    }
                    misses = 0;
                    if (columns is null)
                    {
                        order = custom?.Select(choice => Array.FindIndex(row, value => value.ParameterId == choice.Id)).ToArray()
                            ?? Enumerable.Range(0, row.Length).ToArray();
                        if (order.Length != row.Length || order.Contains(-1))
                            throw new InvalidDataException("The PCM returned a different set of channels than the recording profile.");
                        columns = order.Select(index =>
                        {
                            var value = row[index];
                            int? pid = database.TryGetParameter<PidParameter>(value.ParameterId, out var parameter)
                                ? (int)parameter.PID : null;
                            string name = custom?.FirstOrDefault(choice => choice.Id == value.ParameterId)?.Label
                                ?? value.ParameterName;
                            return new LiveColumn(value.ParameterId, name, value.Units, pid);
                        }).ToArray();
                        writer.WriteLine("Time (s)," + string.Join(",", columns.Select(column =>
                            CsvLog.Escape(column.Unit.Length == 0 ? column.Name : $"{column.Name} [{column.Unit}]"))));
                        status("Recording · " + Path.GetFileName(path));
                    }
                    if (row.Length != columns.Length ||
                        order!.Where((source, index) => row[source].ParameterId != columns[index].Id).Any())
                        throw new InvalidDataException("The adapter changed its channel order during recording.");

                    long milliseconds = clock.ElapsedMilliseconds;
                    var values = order!.Select(index => CsvLog.ParseReading(milliseconds, row[index].ValueAsString)).ToArray();
                    writer.WriteLine((milliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)
                        + "," + string.Join(",", values.Select(value => CsvLog.Escape(value.CsvValue))));
                    if (milliseconds - lastFlushMs >= 1000)
                    {
                        await writer.FlushAsync(token);
                        lastFlushMs = milliseconds;
                    }
                    reading(new LiveReading(milliseconds, columns, values));
                }
            }
            finally
            {
                await vehicle.ResetConnection();
            }
        });
    }

    public static async Task<string[]> ReadCodes(string port, Action<string> status, CancellationToken token)
    {
        var result = new SortedSet<string>();
        await WithVehicle(port, status, token, async (vehicle, _) =>
        {
            await vehicle.SendMessage(new Message([0x68, 0x6A, 0xF0, 0x03]));
            bool responded = false;
            for (int i = 0; i < 12 && !token.IsCancellationRequested; i++)
            {
                var message = await vehicle.ReceiveMessage();
                var data = message?.GetBytes();
                if (data is null || data.Length < 4 || data[3] != 0x43)
                    continue;
                responded = true;
                for (int j = 4; j + 1 < data.Length; j += 2)
                {
                    int a = data[j];
                    int b = data[j + 1];
                    if (a != 0 || b != 0)
                        result.Add($"{"PCBU"[a >> 6]}{(a >> 4) & 3}{a & 15:X}{b:X2}");
                }
            }
            token.ThrowIfCancellationRequested();
            if (!responded)
                throw new IOException("The PCM did not reply to the stored-code request.");
        });
        return result.ToArray();
    }

    private static async Task WithVehicle(string portName, Action<string> status, CancellationToken token,
        Func<Vehicle, ScannerLogger, Task> run)
    {
        if (!portName.StartsWith("/dev/", StringComparison.Ordinal))
            throw new IOException("Enter the adapter’s Linux device path, such as /dev/ttyUSB0.");
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        var logger = new ScannerLogger(status);
        using var port = new LinuxSerialPort(portName);
        var device = new OBDXProDevice(port, logger);
        var protocol = new Protocol();
        using var vehicle = new Vehicle(device, protocol, logger, new ToolPresentNotifier(device, protocol, logger), Assets);
        try
        {
            status("Connecting to OBDX Pro GT…");
            if (!await vehicle.ResetConnection())
                throw new IOException("Could not initialize the OBDX adapter. Check USB, serial permissions, and vehicle power.");
            token.ThrowIfCancellationRequested();
            await run(vehicle, logger);
        }
        finally
        {
            vehicle.ShutdownSignalSource.Cancel();
        }
    }

    private static ParameterDatabase Database()
    {
        var db = new ParameterDatabase(Assets);
        db.LoadDatabase();
        return db;
    }

    private static string ProfilePath(string profile) =>
        Profiles.Contains(profile) ? Path.Combine(Assets, profile + ".LogProfile")
            : throw new ArgumentException("Unknown live logging profile.");

    private sealed class ScannerLogger(Action<string> status) : ILogger
    {
        public void AddUserMessage(string message) => status(message);
        public void AddDebugMessage(string message) { }
        public void StatusUpdateActivity(string message) { }
        public void StatusUpdateTimeRemaining(string message) { }
        public void StatusUpdatePercentDone(string message) { }
        public void StatusUpdateRetryCount(string message) { }
        public void StatusUpdateProgressBar(double completed, bool visible) { }
        public void StatusUpdateKbps(string message) { }
        public void StatusUpdateReset() { }
    }
}
