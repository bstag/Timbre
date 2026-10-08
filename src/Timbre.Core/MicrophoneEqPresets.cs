namespace Timbre.Core;

public static class MicrophoneEqPresets
{
    // Left-to-right labels from the user's B20 Gaming Suite screenshot (2026-10-05).
    // This is UI frequency mapping, not a measured filter response or a mapping for other models.
    public static IReadOnlyList<string> B20BandLabels { get; } = Array.AsReadOnly(new[] {
        "64 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"
    });
    public static MicrophoneEqualizer Flat => MicrophoneEqualizer.Flat;
    // Recovered from GamingSuite.UI.Services.Constants.Constant. Knob values
    // are converted by the Suite's own round(value / 127 * 6, 2) mapping.
    public static MicrophoneEqualizer Warm { get; } = Convert([127, 127, 85, -11, -64, -49, 22, -64, 0]);
    public static MicrophoneEqualizer Clear { get; } = Convert([-127, -127, -64, 0, 0, 22, 43, 0, 0]);
    private static MicrophoneEqualizer Convert(int[] knobs) => MicrophoneEqualizer.FromLevels(knobs.Select(v => (float)Math.Round(v / 127d * 6, 2)).ToArray());
    public static string Name(MicrophoneEqualizer eq) => eq == Flat ? "Flat" : eq == Warm ? "Warm" : eq == Clear ? "Clear" : "Custom";
}
