using System.Buffers.Binary;
using WrongWay;

if (args is not [var bin, var xdf, var reference])
    throw new ArgumentException("Provide the stock 811 BIN, its XDF, and a second 811 BIN.");
var image = new TuneImage(bin);
var comparison = new TuneImage(reference);
var definition = TuneDefinition.Load(xdf, image);
var items = definition.Parameters;
Check(image.Os == 12587811 && image.CompatibleWith(comparison), "Matching P59 OS and segments");
int cellsChecked = 0;
foreach (TuneParameter parameter in items)
for (int row = 0; row < parameter.Rows; row++)
for (int column = 0; column < parameter.Columns; column++)
{
    double value = image.Read(parameter, row, column);
    string displayed = parameter.Format(value, forEdit: true);
    if (!parameter.IsText && parameter.ParseEdit(displayed, setValue: true) != value)
        throw new InvalidDataException($"Edit format did not round-trip for {parameter.Title}, cell {row + 1}, {column + 1}.");
    cellsChecked++;
}
Check(cellsChecked > 0, $"Decoded {cellsChecked:N0} cells and round-tripped numeric edit text");
var bytes = File.ReadAllBytes(bin);
ushort Word(int address) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(address));

TuneParameter Find(string prefix) => items.Single(item => item.Title.StartsWith(prefix, StringComparison.Ordinal));
var ve = Find("B0101 ");
var maf = Find("B5001 ");
var spark = Find("B5913 ");
var afr = Find("B3601 ");
var volume = Find("B0104 ");
var flag = Find("G0106 ");
var os = Find("Operating System ID");
var vin1 = Find("VIN 1");
var vin2 = Find("VIN 2");
var level = Find("Engine Calibration Level");
var ratios = Find("D1201 ");
var checksum = Find("Transmission Calibration Checksum");
var bcc = Find("Broadcast Code (BCC) 1");
var serial = Find("PCM Serial Number 1");
Check(items.Count(item => item.IsText) == 21, "All 21 text fields in the 811 XDF were loaded");
Check(vin1.IsText && vin2.IsText && !vin1.Editable && !vin2.Editable, "Both VIN slots use the read-only text display");
Check(image.ReadText(vin1) == System.Text.Encoding.ASCII.GetString(bytes, 0x4021, 17),
    "First VIN decodes to its 17 stored characters");
Reject<InvalidOperationException>(() => image.ApplyText(vin1, "1GNEK13T36R000001", items), "VIN editing rejected");
Check(image.ReadText(bcc) == System.Text.Encoding.ASCII.GetString(bytes, 0x401C, 4) &&
    image.ReadText(serial) == System.Text.Encoding.ASCII.GetString(bytes, 0x4008, 12), "Broadcast code and PCM serial display as text");
Check(!checksum.Editable && checksum.Display == TuneFormat.Hex &&
    checksum.Format(image.Read(checksum, 0, 0)) == $"0x{Word(0x194A0):X4}", "Checksums use hexadecimal");
Check(checksum.ParseEdit("0x2A", setValue: true) == 42 &&
    checksum.ParseEdit("42", setValue: true) == 42, "Hex and decimal input decode to the same value");
Check(ratios.Editable && ratios.AddressOf(4, 0) == 0x15104, "Reverse gear ratio uses its own mapped address");
Near(image.Read(ratios, 4, 0), Word(0x15104) / 4096.0, 1e-8, "Reverse gear ratio decoded");
Check(Find("Calibration Segment ID").AddressOf(1, 0) == 0x8004 &&
    !Find("Calibration Segment Checksum").Editable, "Segment metadata honors overrides and remains read-only");
Check(flag.Format(0) == "Off" && flag.Format(1) == "On" && flag.ParseEdit("On", setValue: true) == 1 &&
    flag.ParseEdit("off", setValue: true) == 0, "Switch display and input use On and Off");
Check(!os.Editable && !image.CanWrite(0x8000, 2), "Protected OS and checksum storage");
Check(ve.Editable && maf.Editable && spark.Editable && afr.Editable && flag.Editable, "Core calibration items editable");
double cylinder = Word(0x8760) / 32.768;
Near(image.Read(ve, 3, 2), Word(0x8446 + (2 * 20 + 3) * 2) / 0.1767711646826163 / cylinder, 1e-8, "Column-major linked VE");
Near(image.Read(maf, 1, 0), Word(0xF4FE) / 128.0, 1e-8, "MAF scaling");
Near(image.Read(spark, 3, 2), unchecked((short)Word(0x10542 + (2 * 25 + 3) * 2)) / 45.5, 1e-8, "Signed spark scaling");
Near(image.Read(afr, 2, 0), 4096.0 / Word(0xC3FE), 1e-8, "Reciprocal AFR scaling");

double initial = image.Read(ve, 0, 0);
Reject<ArgumentOutOfRangeException>(() => image.Apply(ve, [(0, 0, 54), (1, 0, 1000)], items), "Out-of-range batch");
Check(image.Read(ve, 0, 0) == initial && !image.Dirty, "Rejected batch made no changes");
Reject<InvalidOperationException>(() => image.Apply(volume, [(0, 0, 0)], items), "Undefined linked VE conversion");
Check(!image.Dirty, "Rejected linked change rolled back");
image.Apply(ve, [(0, 0, initial + 1), (1, 0, image.Read(ve, 1, 0) + 1)], items);
double edited = image.Read(ve, 0, 0);
Near(edited, initial + 1, .01, "Quantized VE edit");
image.Undo();
Check(!image.Dirty && image.Read(ve, 0, 0) == initial, "Undo batch");
image.Redo();
Check(image.Dirty && image.Read(ve, 0, 0) == edited, "Redo batch");
var rect = new HashSet<(int Row, int Column)>(new TuneArea(0, 0, 2, 2).Positions());
double Impulse((int Row, int Column) cell) => cell == (1, 1) ? 80 : 0;
double Center(IEnumerable<(int Row, int Column, double Value)> result) =>
    result.Single(item => (item.Row, item.Column) == (1, 1)).Value;
Check(Center(TuneGridTools.Smooth(rect, Impulse, TuneDirection.Both)) == 20 &&
    Center(TuneGridTools.Smooth(rect, Impulse, TuneDirection.Horizontal)) == 40 &&
    Center(TuneGridTools.Smooth(rect, Impulse, TuneDirection.Vertical)) == 40, "Smoothing uses the unmodified source for each direction");
var sparse = new HashSet<(int Row, int Column)> { (1, 1), (1, 2), (2, 0) };
Check(TuneGridTools.Smooth(sparse, Impulse, TuneDirection.Horizontal).All(item => sparse.Contains((item.Row, item.Column))),
    "Smoothing emits only selected cells");
string[] breakpoints = ["0", "10", "40"];
double Coordinate((int Row, int Column) cell) => cell.Column == 2 ? 40 : 0;
Check(Center(TuneGridTools.Interpolate(rect, Coordinate, breakpoints, breakpoints, TuneDirection.Horizontal)) == 10 &&
    Center(TuneGridTools.Interpolate(rect, Coordinate, breakpoints, breakpoints, TuneDirection.Both)) == 10,
    "Line and surface interpolation respect uneven axis spacing");
Reject<FormatException>(() => TuneGridTools.Interpolate(sparse, Coordinate, breakpoints, breakpoints, TuneDirection.Both).ToArray(),
    "A sparse selection cannot define a rectangular interpolation surface");
double ReadVe((int Row, int Column) cell) => image.Read(ve, cell.Row, cell.Column);
string tableCopy = TuneGridTools.Copy(ve, sparse, ReadVe, true);
var pasted = TuneGridTools.Paste(ve, tableCopy, rect, ReadVe, TunePaste.Replace).ToArray();
Check(pasted.Length == sparse.Count && pasted.All(item => sparse.Contains((item.Row, item.Column)) &&
    item.Value == ReadVe((item.Row, item.Column))), "Axis-aligned paste preserves values and skips unselected holes");
var halfChange = TuneGridTools.Paste(ve, "80", rect, _ => 100, TunePaste.MultiplyHalfPercent).ToArray();
Check(halfChange.Length == rect.Count && halfChange.All(cell => cell.Value == 90), "Half-percent paste applies half the relative change");
double[] beforeSmooth = rect.Select(ReadVe).ToArray();
image.Apply(ve, TuneGridTools.Smooth(rect, ReadVe, TuneDirection.Both), items);
Check(!beforeSmooth.SequenceEqual(rect.Select(ReadVe)), "Smoothing writes a decoded edit to the P59 map");
image.Undo();
Check(beforeSmooth.SequenceEqual(rect.Select(ReadVe)), "Undo restores the complete smoothing batch");
image.Apply(afr, [(0, 0, 14.5)], items);
Near(image.Read(afr, 0, 0), 14.5, .03, "Quantized reciprocal AFR edit");
image.Apply(flag, [(0, 0, 1 - image.Read(flag, 0, 0))], items);

string folder = Directory.CreateTempSubdirectory("wrongway-tune-test-").FullName;
try
{
    const string sampleVin = "1GNEK13T36R000001";
    byte[] vinFixture = (byte[])bytes.Clone();
    Array.Fill(vinFixture, (byte)0xFF, 0x4021, 17);
    System.Text.Encoding.ASCII.GetBytes(sampleVin).CopyTo(vinFixture, 0x6021);
    string vinPath = Path.Combine(folder, "vin-slots.bin");
    File.WriteAllBytes(vinPath, vinFixture);
    var vinImage = new TuneImage(vinPath);
    Check(vinImage.ReadText(vin1) == new string('·', 17) && vinImage.Read(vin1, 0, 0) == 0xFF,
        "Blank VIN slot displays placeholders and retains the original byte");
    Check(vinImage.ReadText(vin2) == sampleVin, "Second VIN decodes independently of the first");
    Check(vin2.Format(0) == "·" && vin2.Format(0x4A) == "J", "VIN character formatting");
    string initialLevel = vinImage.ReadText(level);
    Reject<FormatException>(() => vinImage.ApplyText(level, "A", items), "Short text rejected");
    Reject<FormatException>(() => vinImage.ApplyText(level, "Bé", items), "Non-ASCII text rejected");
    Check(!vinImage.Dirty, "Rejected text edits made no changes");
    string newLevel = initialLevel == "BZ" ? "BY" : "BZ";
    vinImage.ApplyText(level, newLevel, items);
    Check(vinImage.ReadText(level) == newLevel, "Text edit wrote its decoded characters");
    vinImage.Undo();
    Check(!vinImage.Dirty && vinImage.ReadText(level) == initialLevel, "Undo restored text as one edit");
    vinImage.Redo();
    vinImage.Apply(ratios, [(4, 0, 2.5)], items);
    string textCopy = Path.Combine(folder, "text-copy.bin");
    vinImage.SaveCopy(textCopy);
    var reopenedText = new TuneImage(textCopy);
    Check(reopenedText.ReadText(level) == newLevel && reopenedText.Read(ratios, 4, 0) == 2.5,
        "Text and reverse ratio reopened with valid checksums");
    var textChanges = new HashSet<int> { 0x8000, 0x8001, 0x8008, 0x8009, 0x15104, 0x15105 };
    byte[] changedTextBytes = File.ReadAllBytes(textCopy);
    Check(Enumerable.Range(0, vinFixture.Length).All(i => vinFixture[i] == changedTextBytes[i] || textChanges.Contains(i)),
        "Only the mapped text, reverse ratio and checksum bytes changed");

    string path = Path.Combine(folder, "edited.bin");
    image.SaveCopy(path);
    var reopened = new TuneImage(path);
    Check(!image.Dirty && reopened.Read(ve, 0, 0) == edited, "Save and reopen with valid checksums");
    byte[] output = File.ReadAllBytes(path);
    Check((output[0x1FBF2] ^ bytes[0x1FBF2]) == 1, "Switch changed only its masked bit");
    var expectedChanges = new HashSet<int> { 0x8000, 0x8001, 0x1FA90, 0x1FA91, 0x1FBF2, 0x8446, 0x8447, 0x8448, 0x8449, 0xC3FA, 0xC3FB };
    Check(Enumerable.Range(0, bytes.Length).All(i => bytes[i] == output[i] || expectedChanges.Contains(i)),
        "Only edited cells and their checksums changed");
    Check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(bin)), "Original BIN unchanged");
    Reject<IOException>(() => image.SaveCopy(path), "Existing destination rejected");
    string wrongXdf = Path.Combine(folder, "wrong.xdf");
    File.WriteAllText(wrongXdf, File.ReadAllText(xdf).Replace("12587811 complete XDF", "12587603 complete XDF"));
    Reject<InvalidDataException>(() => TuneDefinition.Load(wrongXdf, image), "Mismatched XDF OS");
}
finally
{
    Directory.Delete(folder, recursive: true);
}

static void Check(bool result, string label)
{
    if (!result)
        throw new InvalidDataException(label);
    Console.WriteLine($"PASS {label}");
}

static void Near(double value, double expected, double tolerance, string label) =>
    Check(Math.Abs(value - expected) < tolerance, $"{label} ({value:G8})");

static void Reject<T>(Action action, string label) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, label); return; }
    Check(false, label);
}
