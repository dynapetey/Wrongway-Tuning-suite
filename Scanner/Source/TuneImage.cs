using System.Buffers.Binary;
using PcmHacking;

namespace WrongWay;

sealed class TuneImage
{
    private sealed record Segment(int Start, int End);
    private sealed record Edit(int Address, byte[] Before, byte[] After);
    private readonly byte[] data;
    private readonly Segment[] segments;
    private readonly Stack<Edit[]> undo = new();
    private readonly Stack<Edit[]> redo = new();
    private byte[] saved;
    public string Path { get; }
    public uint Os { get; }
    public bool Dirty => !data.AsSpan().SequenceEqual(saved);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public TuneImage Snapshot() => new(this);

    private TuneImage(TuneImage source)
    {
        data = (byte[])source.data.Clone();
        saved = data;
        segments = source.segments;
        Path = source.Path;
        Os = source.Os;
    }

    public TuneImage(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != 1024 * 1024)
            throw new InvalidDataException("The tuning editor requires a full 1 MiB P59 BIN.");
        data = new byte[1024 * 1024];
        stream.ReadExactly(data);
        Path = System.IO.Path.GetFullPath(path);
        var validator = new FileValidator(data, new ValidationLog());
        if (validator.GetFileType() != PcmType.P59)
            throw new InvalidDataException("The file is not a recognized P59 image.");
        Os = validator.GetOsidFromImage();
        if (Os is 0 or uint.MaxValue)
            throw new InvalidDataException("The BIN has an invalid operating system ID.");

        segments = Enumerable.Range(0, 8).Select(index =>
        {
            int offset = 0x50C + index * 8;
            int start = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            int end = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4));
            if (start < 0 || end >= data.Length || end - (long)start < 7 || (start & 1) != 0 || (end & 1) != 1)
                throw new InvalidDataException("The BIN contains an invalid segment range.");
            return new Segment(start, end);
        }).ToArray();
        if (segments[0].Start != 0 || segments[0].End < data.Length - 3)
            throw new InvalidDataException("The BIN has an unsupported OS checksum range.");
        int previousEnd = 0x7FFF;
        foreach (Segment segment in segments.Skip(1).OrderBy(segment => segment.Start))
        {
            if (segment.Start <= previousEnd || segment.End >= 0x20000)
                throw new InvalidDataException("The BIN has an unsupported calibration segment layout.");
            previousEnd = segment.End;
        }

        if (!validator.IsValid())
            throw new InvalidDataException("The BIN failed PCM Hammer checksum validation.");
        saved = (byte[])data.Clone();
    }

    public bool Contains(int address, int width) => address >= 0 && address <= data.Length - width;

    public bool CanWrite(int address, int width) => segments.Skip(1)
        .Any(segment => address >= segment.Start + 8 && address <= segment.End - width + 1);

    public double Read(TuneParameter parameter, int row, int column)
    {
        uint bits = ReadBits(data, parameter, parameter.AddressOf(row, column));
        if (parameter.IsText)
            return bits;
        if (parameter.Mask != 0)
            return (bits & parameter.Mask) != 0 ? 1 : 0;

        double raw;
        if (parameter.Floating)
            raw = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        else if (!parameter.Signed)
            raw = bits;
        else
            raw = parameter.Width switch
            {
                1 => unchecked((sbyte)bits),
                2 => unchecked((short)bits),
                _ => unchecked((int)bits)
            };

        var conversion = parameter.Conversion(this);
        double value = conversion.Decode(raw);
        return double.IsFinite(value) ? value : throw new InvalidDataException("The decoded value is not finite.");
    }

    public string ReadText(TuneParameter parameter) => string.Concat(Enumerable.Range(0, parameter.Columns)
        .Select(column => parameter.Format(Read(parameter, 0, column))));

    public void ApplyText(TuneParameter parameter, string text, IReadOnlyList<TuneParameter> definitions)
    {
        if (!parameter.IsText || !parameter.Editable)
            throw new InvalidOperationException("This text field is read-only.");
        if (text.Length != parameter.Columns)
            throw new FormatException($"Enter exactly {parameter.Columns} characters.");

        var edits = new List<(int Row, int Column, double Value)>();
        for (int column = 0; column < text.Length; column++)
        {
            char character = text[column];
            double existing = Read(parameter, 0, column);
            if (character == '·' && parameter.Format(existing) == "·")
                continue;
            if (character is < ' ' or > '~')
                throw new FormatException("Enter printable ASCII characters. Dots marking blank bytes can be kept in place.");
            if (character != existing)
                edits.Add((0, column, character));
        }
        Apply(parameter, edits, definitions);
    }

    public void Apply(TuneParameter parameter, IEnumerable<(int Row, int Column, double Value)> values,
        IReadOnlyList<TuneParameter> definitions)
    {
        if (!parameter.Editable)
            throw new InvalidOperationException("This item is read-only.");

        var edits = new List<Edit>();
        var addresses = new HashSet<int>();
        var editedCells = new List<(int Row, int Column)>();
        foreach (var (row, column, value) in values)
        {
            int address = parameter.AddressOf(row, column);
            if (!CanWrite(address, parameter.Width) || !addresses.Add(address))
                throw new InvalidOperationException("The selection contains an invalid calibration address.");
            uint bits = Encode(parameter, address, value);
            byte[] after = new byte[parameter.Width];
            for (int index = 0; index < after.Length; index++)
            {
                int destination = parameter.LittleEndian ? index : after.Length - 1 - index;
                after[destination] = (byte)(bits >> (index * 8));
            }
            byte[] before = data.AsSpan(address, parameter.Width).ToArray();
            if (!after.AsSpan().SequenceEqual(before))
            {
                edits.Add(new Edit(address, before, after));
                editedCells.Add((row, column));
            }
        }
        if (edits.Count == 0)
            return;
        Edit[] batch = edits.ToArray();
        Restore(batch, useAfter: true);
        try
        {
            foreach (var (row, column) in editedCells)
                Read(parameter, row, column);
            foreach (TuneParameter dependent in definitions.Where(item => item.Link is not null))
                dependent.Conversion(this);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            Restore(batch, useAfter: false);
            throw new InvalidOperationException("The edit would make a linked or scaled value undefined.", ex);
        }
        undo.Push(batch);
        redo.Clear();
    }

    private uint Encode(TuneParameter parameter, int address, double value)
    {
        if (!double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "A value is outside the XDF range.");
        uint current = ReadBits(data, parameter, address);
        if (parameter.Mask != 0)
            return value switch
            {
                0 => current & ~parameter.Mask,
                1 => current | parameter.Mask,
                _ => throw new ArgumentOutOfRangeException(nameof(value), "Enter On or Off.")
            };

        double raw = parameter.Conversion(this).Encode(value);
        if (parameter.Floating)
        {
            if (!float.IsFinite((float)raw))
                throw new ArgumentOutOfRangeException(nameof(value), "The value exceeds the BIN storage range.");
            return unchecked((uint)BitConverter.SingleToInt32Bits((float)raw));
        }

        int bits = parameter.Width * 8;
        double upper = Math.ScaleB(1, parameter.Signed ? bits - 1 : bits) - 1;
        double lower = parameter.Signed ? -upper - 1 : 0;
        double rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(rounded) || rounded < lower || rounded > upper)
            throw new ArgumentOutOfRangeException(nameof(value), "The value exceeds the BIN storage range.");
        return unchecked((uint)(long)rounded);
    }

    private static uint ReadBits(byte[] image, TuneParameter parameter, int address)
    {
        uint bits = 0;
        for (int index = 0; index < parameter.Width; index++)
        {
            int source = address + (parameter.LittleEndian ? parameter.Width - 1 - index : index);
            bits = (bits << 8) | image[source];
        }
        return bits;
    }

    public void Undo()
    {
        if (!undo.TryPop(out Edit[]? batch))
            return;
        Restore(batch, useAfter: false);
        redo.Push(batch);
    }

    public void Redo()
    {
        if (!redo.TryPop(out Edit[]? batch))
            return;
        Restore(batch, useAfter: true);
        undo.Push(batch);
    }

    private void Restore(Edit[] batch, bool useAfter)
    {
        foreach (Edit edit in batch)
            (useAfter ? edit.After : edit.Before).CopyTo(data, edit.Address);
    }

    public bool CompatibleWith(TuneImage other) => Os == other.Os && segments.SequenceEqual(other.segments);

    public void SaveCopy(string path)
    {
        string target = System.IO.Path.GetFullPath(path);
        if (File.Exists(target))
            throw new IOException("Choose a new BIN filename. This editor saves a separate copy.");
        byte[] output = (byte[])data.Clone();
        for (int index = 1; index < segments.Length; index++)
            Repair(output, segments[index], segments[index].Start);
        Repair(output, segments[0], 0x500);
        if (!new FileValidator(output, new ValidationLog()).IsValid())
            throw new InvalidDataException("The edited BIN failed PCM Hammer checksum validation.");

        string folder = System.IO.Path.GetDirectoryName(target)!;
        string temporary = System.IO.Path.Combine(folder, $".wrongway-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(output);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
            saved = (byte[])data.Clone();
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void Repair(byte[] image, Segment segment, int storage)
    {
        uint sum = 0;
        int address = segment.Start == 0 ? 0 : segment.Start + 2;
        while (address <= segment.End)
        {
            if (address == 0x4000)
                address = 0x20000; // Skip the parameter and calibration ranges for the OS checksum.
            if (address > segment.End)
                break;
            if (address != storage)
                sum += BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(address));
            address += 2;
        }
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(storage), unchecked((ushort)-sum));
    }

    private sealed class ValidationLog : ILogger
    {
        public void AddUserMessage(string message) { }
        public void AddDebugMessage(string message) { }
        public void StatusUpdateActivity(string activity) { }
        public void StatusUpdateTimeRemaining(string remaining) { }
        public void StatusUpdatePercentDone(string percent) { }
        public void StatusUpdateRetryCount(string retries) { }
        public void StatusUpdateProgressBar(double completed, bool visible) { }
        public void StatusUpdateKbps(string Kbps) { }
        public void StatusUpdateReset() { }
    }
}
