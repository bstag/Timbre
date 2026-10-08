using System.Text.Json;
using System.Text.Json.Serialization;
using Timbre.Core;

internal static class HardwareAudioTests
{
    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Audio validation needs Windows."); return 1; }
        var exit = 1;
        var thread = new Thread(() => {
            string Value(string name) { var i = Array.IndexOf(args, name); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + name); return args[i + 1]; }
            object result;
            try {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                var productId = Value("--hardware-audio");
                if (!new[] { "009f", "0098" }.Contains(productId, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Audio diagnostics support only the mapped B20 and GSX 300 microphone controls.");
                var audio = new CoreAudioBackend();
                var endpoint = audio.Discover().Single(e => e.Direction == AudioDirection.Microphone && e.Usb is { } usb &&
                    usb.VendorId.Equals("1395", StringComparison.OrdinalIgnoreCase) && usb.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase));
                var effects = WindowsApoMemory.Create(audio); var initial = effects.Read(endpoint);
                using var source = new WasapiMeasurementSource(endpoint);
                if (!ApoMicrophoneCodec.Matches(effects.Read(endpoint), initial)) throw new InvalidOperationException("Opening audio changed EPOS processing state. No test writes were attempted; reload the controls.");
                Console.WriteLine("Measuring gate off / maximum / off. Keep ambient input steady for about 8 seconds.");
                var report = GateAudioValidation.Run(endpoint, audio, effects, source, TimeSpan.FromSeconds(2));
                result = new { endpoint.Name, endpoint.ProfileIdentity, source.Format, Scope = "Shared-mode Windows microphone RMS; gate only, ambient-input diagnostic, no audio saved", Report = report };
                exit = report.Outcome == AudioValidationOutcome.Passed && report.SettingsRestored ? 0 : report.Outcome == AudioValidationOutcome.Inconclusive && report.SettingsRestored ? 2 : 1;
                Console.WriteLine($"Audio validation: {report.Outcome}. {report.Reason} Settings restored: {report.SettingsRestored}");
            } catch (Exception ex) { result = new { Outcome = "Failed", Error = ex.ToString(), Scope = "Audio validation setup or cleanup failed" }; Console.Error.WriteLine(ex.Message); exit = 1; }
            try {
                var path = Path.GetFullPath(Value("--report")); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var options = new JsonSerializerOptions { WriteIndented = true }; options.Converters.Add(new JsonStringEnumConverter());
                File.WriteAllText(path, JsonSerializer.Serialize(result, options));
            } catch (Exception ex) { Console.Error.WriteLine(ex.Message); exit = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
}
