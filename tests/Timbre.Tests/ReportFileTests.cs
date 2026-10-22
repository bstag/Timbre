using System.Text;
using Timbre.Core;

internal static class ReportFileTests
{
    public static void Run(TestSuite suite)
    {
        suite.Case("Report reader permits atomic replacement while retaining its complete old snapshot", () => {
            using var rig = new Rig(); AtomicReportFile.Write(rig.Path, "old");
            using var reader = AtomicReportFile.OpenRead(rig.Path);
            AtomicReportFile.Write(rig.Path, "new");
            using var text = new StreamReader(reader);
            TestSuite.Assert(text.ReadToEnd() == "old" && Encoding.UTF8.GetString(AtomicReportFile.Read(rig.Path, 3)) == "new");
        });
        suite.Case("Report publisher retries an actual Windows reader sharing conflict without losing the old report", () => {
            using var rig = new Rig(); AtomicReportFile.Write(rig.Path, "old");
            using var reader = new FileStream(rig.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var conflicts = 0;
            AtomicReportFile.Write(rig.Path, "new", _ => {
                conflicts++; TestSuite.Assert(File.ReadAllText(rig.Path) == "old"); reader.Dispose();
            });
            TestSuite.Assert(conflicts == 1 && File.ReadAllText(rig.Path) == "new" && Directory.GetFiles(rig.Root, "*.tmp").Length == 0);
        });
        suite.Case("Persistent report lock remains a failure and cleans temporary data without changing the original", () => {
            using var rig = new Rig(); AtomicReportFile.Write(rig.Path, "original");
            using var reader = new FileStream(rig.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var failed = false;
            try { AtomicReportFile.Write(rig.Path, "replacement"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            TestSuite.Assert(failed && File.ReadAllText(rig.Path) == "original" && Directory.GetFiles(rig.Root, "*.tmp").Length == 0);
        });
        suite.Case("Report reads reject oversized input and retain exact UTF8 contents at the byte limit", () => {
            using var rig = new Rig(); AtomicReportFile.Write(rig.Path, "\u00e9");
            TestSuite.Assert(AtomicReportFile.Read(rig.Path, 2).SequenceEqual(Encoding.UTF8.GetBytes("\u00e9")));
            TestSuite.Throws<InvalidDataException>(() => AtomicReportFile.Read(rig.Path, 1));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => AtomicReportFile.Read(rig.Path, -1));
        });
        suite.Case("Report publication does not retry non-sharing failures or leave temporary files", () => {
            using var rig = new Rig(); Directory.CreateDirectory(rig.Path);
            var failed = false; var retries = 0;
            try { AtomicReportFile.Write(rig.Path, "new", _ => retries++); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            TestSuite.Assert(failed && retries == 0 && Directory.Exists(rig.Path) && Directory.GetFiles(rig.Root, "*.tmp").Length == 0);
        });
    }
    private sealed class Rig : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "timbre-report-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "report.json");
        public Rig() => Directory.CreateDirectory(Root);
        public void Dispose() {
            if (!System.IO.Path.GetFullPath(Root).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test cleanup path.");
            Directory.Delete(Root, true);
        }
    }
}
