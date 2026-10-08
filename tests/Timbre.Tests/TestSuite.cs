using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

internal sealed class TestSuite
{
    private readonly List<Result> results = [];
    public int Failures => results.Count(r => !r.Passed);
    public void Case(string name, Action action)
    {
        var watch = Stopwatch.StartNew(); string? error = null;
        try { action(); } catch (Exception ex) { error = ex.ToString(); }
        results.Add(new(name, error is null, watch.Elapsed.TotalSeconds, error));
        Console.WriteLine($"{(error is null ? "PASS" : "FAIL")} {name}{(error is null ? "" : ": " + error.Split('\n')[0])}");
    }
    public static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    public static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected rejection before applying settings.");
    }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T ex) { return ex; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    public void Report(string[] args)
    {
        for (var i = 0; i < args.Length; i += 2) {
            if (i + 1 >= args.Length || args[i] is not ("--report" or "--junit")) throw new ArgumentException("Use --report FILE and/or --junit FILE.");
            var path = Path.GetFullPath(args[i + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (args[i] == "--report") File.WriteAllText(path, JsonSerializer.Serialize(new { Passed = Failures == 0, Total = results.Count, Failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
            else new XDocument(new XElement("testsuite", new XAttribute("name", "Timbre"), new XAttribute("tests", results.Count), new XAttribute("failures", Failures),
                results.Select(r => new XElement("testcase", new XAttribute("name", r.Name), new XAttribute("time", r.Seconds), r.Error is null ? null : new XElement("failure", r.Error))))).Save(path);
        }
        Console.WriteLine($"{results.Count - Failures}/{results.Count} checks passed; {Failures} failed.");
    }
    private sealed record Result(string Name, bool Passed, double Seconds, string? Error);
}
