using PcmHacking;
namespace WrongWay;

// Validate the exact DPID payload decoder used by serial WrongWay Scanner.
static class PidDefinitionTests
{
    public static void SelfTest(ParameterDatabase db)
    {
        int checks = 0;
        void AssertValue(string id, string unit, byte a, byte b, double expected, double tolerance = 0.001)
        {
            if (!db.TryGetParameter<PcmParameter>(id, out var parameter))
                throw new InvalidDataException($"Missing PID {id}.");
            var conversion = parameter.GetConversion(unit)
                ?? throw new InvalidDataException($"PID {id} lacks {unit} conversion.");
            var column = new LogColumn(parameter, conversion, false);
            var config = ExpandedLiveLogger.Pack([column]);
            var parser = new LogRowParser(config);
            var payload = new byte[6];
            payload[0] = a;
            payload[1] = b;
            parser.ParseData(new RawLogData(config.ParameterGroups.Single().Dpid, payload));
            double actual = parser.Evaluate()[column].ValueAsDouble;
            if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
                throw new InvalidDataException($"PID {id} [{unit}] decoded {actual} instead of {expected}");
            checks++;
        }
        foreach (string id in new[] { "O2LeftUpstream", "O2LeftDownstream", "O2RightUpstream",
            "O2RightDownstream", "O2LeftSensor3", "O2LeftSensor4" })
        {
            AssertValue(id, "mV", 0x5A, 0x00, 450);
            AssertValue(id, "mV", 0x5A, 0xFF, 450);
            AssertValue(id, "mV", 0x00, 0xFF, 0);
            AssertValue(id, "mV", 0xB4, 0x80, 900);
        }
        foreach (string oilId in new[] { "EngineOilPressure", "EngineOilPressureSensor" })
        {
            AssertValue(oilId, "kPa", 100, 0, 322.2855, 0.001);
            AssertValue(oilId, "PSI", 100, 0, 322.2855 / 6.894757293168, 0.001);
            AssertValue(oilId, "kPa", 128, 0, 443.413, 0.001);
        }
        AssertValue("FuelSystemStatus", "Raw", 0x02, 0x01, 513);
        AssertValue("VehicleSpeedSensor", "mph", 100, 0, 62.1371192237);
        AssertValue("ShortTermFTLeftBank", "%", 160, 0, 25);
        AssertValue("ShortTermFTRightBank", "%", 96, 0, -25);
        AssertValue("EngineSpeed", "RPM", 0x1F, 0x40, 2000);
        AssertValue("ECTSensor", "°C", 0x78, 0, 80);
        Console.WriteLine($"SAE PID raw-byte decoding checks passed ({checks} cases).");
    }
}
