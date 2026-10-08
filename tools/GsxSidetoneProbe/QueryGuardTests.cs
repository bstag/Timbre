namespace EposResearch;

internal static class QueryGuardTests
{
    const string Instance = "USB\\VID_1395&PID_0098\\test-gsx";
    static HidInterface Device(string instance = Instance) => new() {
        Path = "\\\\?\\hid#vid_1395&pid_0098&mi_03&col01#test#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        UsbInstanceId = instance, VendorId = "1395", ProductId = "0098", UsagePage = "000C", Usage = "0001",
        InputReportBytes = 35, OutputReportBytes = 39, FeatureReportBytes = 0
    };
    public static int Run()
    {
        var passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
        void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
        void Reject(Action action) {
            try { action(); } catch (InvalidOperationException) { return; } catch (InvalidDataException) { return; }
            throw new Exception("Expected rejection before device access");
        }
        try {
            Check("getter request matches complete native 39-byte buffer", () => {
                var expected = new byte[39]; new byte[] { 4, 0, 1, 0x12, 0x7B }.CopyTo(expected, 0);
                Assert(GsxSidetoneQuery.Request.SequenceEqual(expected));
                var copy = GsxSidetoneQuery.Request; copy[2] = 0x40;
                Assert(GsxSidetoneQuery.Request.SequenceEqual(expected));
            });
            Check("selected physical instance excludes other GSX units", () => {
                var good = Device(); Assert(ReferenceEquals(GsxSidetoneQuery.Select(Instance, new[] { Device(Instance + "-other"), good }), good));
                Reject(() => GsxSidetoneQuery.Select(Instance, new[] { Device(Instance + "-other") }));
            });
            Check("missing and ambiguous collections fail closed", () => {
                Reject(() => GsxSidetoneQuery.Select(Instance, Array.Empty<HidInterface>()));
                Reject(() => GsxSidetoneQuery.Select(Instance, new[] { Device(), Device() }));
            });
            Check("B20 and incomplete physical identities are rejected", () => {
                foreach (var id in new[] { "", "USB\\VID_1395&PID_009F\\b20", "USB\\VID_1395&PID_0098\\", Instance + "\\extra", "USB\\VID_1395&PID_0098&MI_03\\collection" })
                    Reject(() => GsxSidetoneQuery.Select(id, new[] { Device() }));
            });
            Check("wrong collection and vendor cannot route the query", () => {
                foreach (var edit in new Action<HidInterface>[] { d => d.VendorId = "1234", d => d.ProductId = "009F", d => d.UsagePage = "FFFF", d => d.Usage = "0002", d => d.Path = d.Path.Replace("col01", "col02"), d => d.Path = d.Path.Replace("pid_0098", "pid_009f") }) {
                    var device = Device(); edit(device); Reject(() => GsxSidetoneQuery.Select(Instance, new[] { device }));
                }
            });
            Check("unknown report lengths and descriptor errors reject", () => {
                foreach (var edit in new Action<HidInterface>[] { d => d.InputReportBytes = 2, d => d.OutputReportBytes = 2, d => d.FeatureReportBytes = 1, d => d.Error = "Access denied", d => d.Path = "" }) {
                    var device = Device(); edit(device); Reject(() => GsxSidetoneQuery.Select(Instance, new[] { device }));
                }
            });
            Check("canceled capture opens no device handle", () => {
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                try { GsxSidetoneQuery.CaptureAsync(Device(), canceled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                throw new Exception("Cancellation was ignored");
            });
            Check("direct capture cannot bypass descriptor validation", () => {
                var device = Device(); device.ProductId = "009F";
                Reject(() => GsxSidetoneQuery.CaptureAsync(device, CancellationToken.None).GetAwaiter().GetResult());
            });
            Check("restoration accepts only complete known status captures", () => {
                foreach (var malformed in new[] { Array.Empty<byte>(), new byte[34], new byte[35] })
                    Reject(() => GsxSidetoneQuery.RestoreRequest(malformed));
                var response = new byte[35]; response[0] = 5; response[1] = 226;
                var request = GsxSidetoneQuery.RestoreRequest(response);
                var expected = GsxSidetoneQuery.Request; expected[1] = 0x40; expected[5] = 226;
                Assert(request.SequenceEqual(expected));
                response[2] = 1; Reject(() => GsxSidetoneQuery.RestoreRequest(response));
                response[2] = 0; response[1] = 198; Reject(() => GsxSidetoneQuery.RestoreRequest(response));
            });
            Console.WriteLine($"{passed} GSX research guard checks passed; no USB reports sent.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
