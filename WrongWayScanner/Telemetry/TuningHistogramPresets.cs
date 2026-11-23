using System;
using System.Collections.Generic;

namespace WrongWayScanner.Telemetry
{
    public enum HistogramAggregation
    {
        Average,
        Max,
        Min
    }

    public sealed class HistogramPreset
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string TableRef { get; init; } = "";
        public string XChannel { get; init; } = "";
        public string YChannel { get; init; } = "";
        public string ValueChannel { get; init; } = "";
        public double[] XAxis { get; init; } = Array.Empty<double>();
        public double[] YAxis { get; init; } = Array.Empty<double>();
        public HistogramAggregation Aggregation { get; init; } = HistogramAggregation.Average;
        public int MinCellHits { get; init; } = 10;
        public Func<TelemetryFrame, bool> Filter { get; init; } = _ => true;
    }

    public static class TuningHistogramPresets
    {
        public static readonly double[] MapKpaAxis =
            { 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100, 105 };

        public static readonly double[] VeRpmAxis =
            { 400, 800, 1200, 1600, 2000, 2400, 2800, 3200, 3600, 4000, 4400, 4800, 5200, 5600, 6000, 6400, 6800 };

        public static readonly double[] SparkAirmassAxis =
        {
            0.08, 0.12, 0.16, 0.20, 0.24, 0.28, 0.32, 0.36, 0.40, 0.44, 0.48,
            0.52, 0.56, 0.60, 0.64, 0.68, 0.72, 0.76, 0.80, 0.84, 0.88, 0.92,
            0.96, 1.00, 1.04, 1.08, 1.12, 1.16, 1.20
        };

        public static readonly double[] SparkRpmAxis =
            { 400, 600, 800, 1000, 1200, 1600, 2000, 2400, 2800, 3200, 3600, 4000, 4400, 4800, 5200, 5600, 6000, 6400, 6800 };

        public static readonly double[] EctFahrenheitAxis =
            { -40, -22, -4, 14, 32, 50, 68, 86, 104, 122, 140, 158, 176, 194, 212, 230, 248 };

        public static readonly double[] MafHzAxis = BuildLinearAxis(1500, 12000, 125);

        public static readonly double[] TpsPercentAxis =
            { 0, 12.5, 25, 37.5, 50, 62.5, 75, 87.5, 100 };

        public static readonly double[] GearAxis = { 1, 2, 3, 4 };

        public static readonly double[] IdleGearStateAxis = { 0, 1 };

        public static IReadOnlyList<HistogramPreset> CreateAll() => new[]
        {
            new HistogramPreset
            {
                Id = "ve_cl_trims",
                Name = "Main VE - Closed Loop Trim Error (%)",
                TableRef = "Table 12603",
                XChannel = "MAP_kPa",
                YChannel = "RPM",
                ValueChannel = "TotalTrimErrorPct",
                XAxis = MapKpaAxis,
                YAxis = VeRpmAxis,
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 15,
                Filter = f => f.EctF >= 175.0 && !f.IsDfco && f.IsClosedLoop && Math.Abs(f.DeltaTps) < 3.0
            },
            new HistogramPreset
            {
                Id = "ve_ol_lambda",
                Name = "Main VE - Wideband Lambda Error (%)",
                TableRef = "Table 12603",
                XChannel = "MAP_kPa",
                YChannel = "RPM",
                ValueChannel = "LambdaErrorPct",
                XAxis = MapKpaAxis,
                YAxis = VeRpmAxis,
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 10,
                Filter = f => f.EctF >= 175.0 && !f.IsDfco && Math.Abs(f.DeltaTps) < 5.0
            },
            new HistogramPreset
            {
                Id = "maf_calibration",
                Name = "MAF Calibration - Error (%) vs Hz",
                TableRef = "Table 12999",
                XChannel = "MAF_Hz",
                YChannel = "SingleRow",
                ValueChannel = "TotalTrimErrorPct",
                XAxis = MafHzAxis,
                YAxis = new[] { 0.0 },
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 15,
                Filter = f => f.EctF >= 175.0 && !f.IsDfco && Math.Abs(f.DeltaTps) < 3.0
            },
            new HistogramPreset
            {
                Id = "knock_retard_max",
                Name = "Knock Retard (deg) - MAX",
                TableRef = "High/Low Octane Spark",
                XChannel = "CylAirmass_gCyl",
                YChannel = "RPM",
                ValueChannel = "KnockRetardDeg",
                XAxis = SparkAirmassAxis,
                YAxis = SparkRpmAxis,
                Aggregation = HistogramAggregation.Max,
                MinCellHits = 1,
                Filter = _ => true
            },
            new HistogramPreset
            {
                Id = "spark_advance_avg",
                Name = "Spark Advance (deg) - AVG",
                TableRef = "High Octane Spark",
                XChannel = "CylAirmass_gCyl",
                YChannel = "RPM",
                ValueChannel = "SparkAdvanceDeg",
                XAxis = SparkAirmassAxis,
                YAxis = SparkRpmAxis,
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 5,
                Filter = f => f.EctF >= 175.0 && !f.IsDfco
            },
            new HistogramPreset
            {
                Id = "idle_raf_error",
                Name = "Idle RAF - STIT + LTIT (g/s)",
                TableRef = "Base Running Airflow",
                XChannel = "ECT_F",
                YChannel = "InGearState",
                ValueChannel = "TotalIdleAirflowError_gs",
                XAxis = EctFahrenheitAxis,
                YAxis = IdleGearStateAxis,
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 20,
                Filter = f => f.VssMph < 2.0 && f.TpsPct < 1.5
            },
            new HistogramPreset
            {
                Id = "injector_duty_cycle_max",
                Name = "Injector Duty Cycle (%) - MAX",
                TableRef = "Fuel Safety (<85%)",
                XChannel = "MAP_kPa",
                YChannel = "RPM",
                ValueChannel = "InjectorDutyCyclePct",
                XAxis = MapKpaAxis,
                YAxis = VeRpmAxis,
                Aggregation = HistogramAggregation.Max,
                MinCellHits = 1,
                Filter = _ => true
            },
            new HistogramPreset
            {
                Id = "trans_tcc_slip",
                Name = "4L60E TCC Slip (RPM) - AVG",
                TableRef = "TCC Apply / PWM",
                XChannel = "TPS_Pct",
                YChannel = "CurrentGear",
                ValueChannel = "TccSlipRpm",
                XAxis = TpsPercentAxis,
                YAxis = GearAxis,
                Aggregation = HistogramAggregation.Average,
                MinCellHits = 10,
                Filter = f => f.TftF >= 120.0 && f.TccLocked
            }
        };

        private static double[] BuildLinearAxis(double start, double end, double step)
        {
            var list = new List<double>();
            for (double v = start; v <= end; v += step)
                list.Add(v);
            return list.ToArray();
        }
    }
}
