// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using PcmHacking;

namespace WrongWay;

sealed record LiveColumn(string Id, string Name, string Unit);
sealed record LiveReading(long Time, LiveColumn[] Columns, Reading[] Values);

static class LiveScanner
{
    private static readonly string Assets = Path.Combine(AppContext.BaseDirectory, "Assets");
    public static readonly string[] Profiles = ["WrongWayP59", "Basics", "FuelTrims"];

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
        string port, string profileName, string path, Action<string> status,
        Action<LiveReading> reading, CancellationToken token)
    {
        if (File.Exists(path))
            throw new IOException("Choose a new CSV filename so the previous log is preserved.");

        await WithVehicle(port, status, token, async (vehicle, logger) =>
        {
            var os = await vehicle.QueryOperatingSystemId(token);
            if (os.Status != ResponseStatus.Success)
                throw new IOException("The PCM did not return its operating system ID.");
            var database = Database();
            var profile = new LogProfileReader(database, os.Value, logger).Read(ProfilePath(profileName));
            if (profile.IsEmpty)
                throw new InvalidDataException("The selected profile contains no channels for this PCM OS.");
            status($"PCM OS {os.Value} · starting {profileName}");
            using var can = new CanLogger(database, logger);
            await can.SetPort(null);
            var scanner = vehicle.CreateLogger(os.Value, can, profile.Columns, logger);
            token.ThrowIfCancellationRequested();
            if (!await scanner.StartLogging())
                throw new IOException("The PCM did not start streaming live data.");

            try
            {
                using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
                writer.AutoFlush = true;
                var clock = Stopwatch.StartNew();
                LiveColumn[]? columns = null;
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
                        columns = row.Select(value => new LiveColumn(value.ParameterId,
                            value.ParameterName, value.Units)).ToArray();
                        writer.WriteLine("Time (s)," + string.Join(",", columns.Select(column =>
                            CsvLog.Escape(column.Unit.Length == 0 ? column.Name : $"{column.Name} [{column.Unit}]"))));
                        status("Recording · " + Path.GetFileName(path));
                    }
                    if (row.Length != columns.Length ||
                        row.Where((value, index) => value.ParameterId != columns[index].Id).Any())
                        throw new InvalidDataException("The adapter changed its channel order during recording.");

                    long milliseconds = clock.ElapsedMilliseconds;
                    var values = row.Select(value => CsvLog.ParseReading(milliseconds, value.ValueAsString)).ToArray();
                    writer.WriteLine((milliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)
                        + "," + string.Join(",", values.Select(value => CsvLog.Escape(value.CsvValue))));
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
