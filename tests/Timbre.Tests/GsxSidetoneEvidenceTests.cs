using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

internal static class GsxSidetoneEvidenceTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("GSX static sidetone tables retain recovered PE data and quantization", () => {
            using var json = Load(); var root = json.RootElement;
            var forward = Values(root, "SliderToHardwareByte"); var inverse = Values(root, "HardwareByteToSlider");
            TestSuite.Assert(forward.Length == 256 && inverse.Length == 256);
            TestSuite.Assert(Hash(forward) == root.GetProperty("SliderArraySha256").GetString());
            TestSuite.Assert(Hash(inverse) == root.GetProperty("InverseArraySha256").GetString());
            TestSuite.Assert(forward.Distinct().Count() == 58 && forward.All(v => v is >= 0 and <= 255));
            TestSuite.Assert(new[] { 0, 64, 127, 128, 191, 255 }.Select(i => forward[i]).SequenceEqual(new[] { 199, 222, 239, 239, 250, 0 }));
            TestSuite.Assert(!root.GetProperty("UsbReportsSent").GetBoolean());
        });
        suite.Case("GSX static sidetone inverse preserves every supported hardware value", () => {
            using var json = Load(); var root = json.RootElement;
            var forward = Values(root, "SliderToHardwareByte"); var inverse = Values(root, "HardwareByteToSlider");
            foreach (var raw in forward.Distinct()) {
                TestSuite.Assert(inverse[raw] is >= 0 and <= 255);
                TestSuite.Assert(forward[inverse[raw]] == raw, "Recovered inverse changed a supported hardware value");
            }
            // Quantization prevents exact UI-position round trips.
            TestSuite.Assert(inverse[forward[64]] == 62 && inverse[0] == 255 && inverse[199] == 0);
        });
        suite.Case("Live GSX sidetone minimum midpoint maximum match Suite conversion", () => {
            using var json = Load(); var forward = Values(json.RootElement, "SliderToHardwareByte");
            foreach (var point in new[] { ("min", 0, 199), ("mid", 127, 239), ("max", 255, 0) }) {
                var report = Status(point.Item1);
                TestSuite.Assert(report.Length == 35 && report[0] == 5 && report.Skip(2).All(b => b == 0));
                TestSuite.Assert(report[1] == point.Item3 && forward[point.Item2] == report[1]);
            }
            // Zero is maximum on this mapping; a future mute control must not encode zero.
            TestSuite.Assert(Status("max")[1] == 0 && Status("min")[1] != 0);
        });
        suite.Case("Live GSX sidetone exact restoration preserves complete baseline status", () => {
            var before = Status("baseline"); var after = Status("restored");
            TestSuite.Assert(before.SequenceEqual(after) && before.Length == 35 && before[0] == 5 && before[1] == 226);
            using var json = Load(); var inverse = Values(json.RootElement, "HardwareByteToSlider");
            var forward = Values(json.RootElement, "SliderToHardwareByte");
            TestSuite.Assert(inverse[before[1]] == 74 && forward[74] == before[1]);
        });
    }
    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gsx-sidetone-ui-map.json")));
    private static byte[] Status(string stage) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"gsx-sidetone-{stage}.bin"));
    private static int[] Values(JsonElement root, string name) => root.GetProperty(name).EnumerateArray().Select(v => v.GetInt32()).ToArray();
    private static string Hash(int[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
