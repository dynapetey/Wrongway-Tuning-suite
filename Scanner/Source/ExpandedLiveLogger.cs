// SPDX-License-Identifier: GPL-3.0-only
using PcmHacking;

namespace WrongWay;

// Additional P59 DPIDs are requested in sets of at most three, rather than
// adding them to both fixed-rate streams. A row is published only when complete.
sealed class ExpandedLiveLogger : Logger
{
    private readonly CancellationToken cancellation;
    private readonly Func<DpidCollection, Task<bool>> request;
    private readonly Func<Task<RawLogData?>> receive;
    private readonly Func<Task> present;
    private DateTime lastPresent = DateTime.UtcNow;

    public ExpandedLiveLogger(Vehicle vehicle, CanLogger can, uint os,
        IEnumerable<LogColumn> columns, ILogger logger, CancellationToken token)
        : this(vehicle, can, os, Pack(columns), logger, token) { }

    private ExpandedLiveLogger(Vehicle vehicle, CanLogger can, uint os,
        DpidConfiguration config, ILogger logger, CancellationToken token)
        : base(vehicle, can, os, config, new MathValueProcessor(config, []), logger)
    {
        cancellation = token;
        request = dpids => vehicle.RequestDpids(dpids, false);
        receive = () => vehicle.ReadLogData();
        present = () => vehicle.SendDataLoggerPresentNotification();
    }

    internal static DpidConfiguration Pack(IEnumerable<LogColumn> columns)
    {
        var result = new DpidConfiguration();
        var source = columns.ToArray();
        if (source.Any(c => c.Parameter is not PcmParameter))
            throw new InvalidDataException("Expanded polling requires PCM inputs; add calculated channels in Maths.");
        if (source.Select(c => c.Parameter.Id).Distinct().Count() != source.Length)
            throw new InvalidDataException("Select each live channel once.");
        foreach (var column in source.OrderByDescending(c => ((PcmParameter)c.Parameter).ByteCount))
        {
            int size = ((PcmParameter)column.Parameter).ByteCount;
            if (size is < 1 or > 2)
                throw new InvalidDataException($"Unsupported live width for {column.Parameter.Name}.");
            var group = result.ParameterGroups.FirstOrDefault(g => g.TotalBytes + size <= ParameterGroup.MaxBytes);
            if (group is null)
            {
                if (result.ParameterGroups.Count >= 6)
                    throw new InvalidDataException("This channel set exceeds the 36-byte polled recording capacity.");
                group = new ParameterGroup((byte)(0xFE - result.ParameterGroups.Count));
                result.ParameterGroups.Add(group);
            }
            if (!group.TryAddLogColumn(column))
                throw new InvalidDataException("Unable to pack live channel " + column.Parameter.Name);
        }
        return result;
    }

    protected override async Task<bool> StartLoggingInternal()
    {
        cancellation.ThrowIfCancellationRequested();
        await Vehicle.SetDeviceTimeout(TimeoutScenario.DataLogging3);
        return true;
    }

    internal static async Task Collect(LogRowParser row, byte[] dpids,
        Func<DpidCollection, Task<bool>> request, Func<Task<RawLogData?>> receive,
        CancellationToken token)
    {
        foreach (var chunk in dpids.Chunk(3))
        {
            token.ThrowIfCancellationRequested();
            if (!await request(new DpidCollection(chunk)))
                throw new IOException("The adapter could not request live data groups.");
            var waiting = chunk.ToHashSet();
            for (int attempt = 0; attempt < chunk.Length * 4 + 4 && waiting.Count > 0; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var raw = await receive();
                if (raw is null) return;
                if (waiting.Contains(raw.Dpid) && raw.Payload.Length == 6)
                {
                    row.ParseData(raw);
                    waiting.Remove(raw.Dpid);
                }
            }
            if (waiting.Count != 0) return;
        }
    }

    protected override async Task GetNextRowInternal(LogRowParser row)
    {
        // Remove late frames from an abandoned previous row before fresh requests.
        Vehicle.ClearDeviceMessageQueue();
        await Collect(row, Dpids.Dpids, request, receive, cancellation);
        if ((DateTime.UtcNow - lastPresent).TotalSeconds >= 2)
        {
            cancellation.ThrowIfCancellationRequested();
            await present();
            lastPresent = DateTime.UtcNow;
        }
    }

    internal static void SelfTest(ParameterDatabase db)
    {
        var choices = LiveScanner.Catalog(LiveScanner.DefaultOs)
            .Where(c => c.Bytes == 1 || c.Bytes == 2).ToArray();
        LogColumn Column(LiveChannelChoice c)
        {
            if (!db.TryGetParameter<PcmParameter>(c.Id, out var p)) throw new InvalidDataException(c.Id);
            return new LogColumn(p, p.GetConversion(c.Units[0]), false);
        }
        var trims = choices.Where(c => c.Pid is >= 6 and <= 9).ToArray();
        if (trims.Length != 4) throw new InvalidDataException("Missing fuel-trim definitions.");
        var selected = trims.Concat(choices.Where(c => c.Bytes == 2).Take(10))
            .Concat(choices.Where(c => c.Bytes == 1 && !trims.Contains(c)).Take(12)).Select(Column).ToArray();
        var config = Pack(selected);
        if (config.ParameterGroups.Count != 6 || config.ParameterGroups.Sum(g => g.TotalBytes) != 36
            || config.ParameterGroups.Any(g => g.TotalBytes > 6)
            || config.AllLogColumns.Count != selected.Length)
            throw new InvalidDataException("Expanded channel packing lost a channel.");
        var extra = choices.First(c => !selected.Any(s => s.Parameter.Id == c.Id));
        try { Pack(selected.Append(Column(extra))); throw new InvalidOperationException("Accepted overflow."); }
        catch (InvalidDataException) { }
        var ids = config.ParameterGroups.Select(g => g.Dpid).ToArray();
        var queue = new Queue<RawLogData?>();
        int requests = 0;
        Task<bool> Request(DpidCollection collection)
        {
            requests++;
            if (collection.Dpids.Length > 3) throw new InvalidDataException("Oversized polling request.");
            queue.Enqueue(new RawLogData(0xF2, new byte[6])); // unrelated group
            foreach (byte id in collection.Dpids.Reverse())
            {
                queue.Enqueue(new RawLogData(id, Enumerable.Repeat((byte)128, 6).ToArray()));
                queue.Enqueue(new RawLogData(id, new byte[6])); // duplicate must not complete another group
            }
            return Task.FromResult(true);
        }
        var row = new LogRowParser(config);
        Collect(row, ids, Request, () => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null), default).GetAwaiter().GetResult();
        if (!row.IsComplete || requests != 2) throw new InvalidDataException("Expanded row did not complete.");
        var values = row.Evaluate();
        foreach (var column in selected.Where(c => trims.Any(t => t.Id == c.Parameter.Id)))
            if (Math.Abs(values[column].ValueAsDouble) > 0.0001)
                throw new InvalidDataException("Fuel trim decoding mismatch.");
        var incomplete = new LogRowParser(config);
        Collect(incomplete, ids, _ => Task.FromResult(true), () => Task.FromResult<RawLogData?>(null), default).GetAwaiter().GetResult();
        if (incomplete.IsComplete) throw new InvalidDataException("Missing group produced a complete row.");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try
        {
            Collect(new LogRowParser(config), ids, _ => throw new InvalidOperationException("Canceled request sent."),
                () => Task.FromResult<RawLogData?>(null), canceled.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation ignored.");
        }
        catch (OperationCanceledException) { }
    }
}
