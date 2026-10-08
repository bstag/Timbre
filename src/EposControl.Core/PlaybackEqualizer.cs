using System.Text.Json.Serialization;

namespace EposControl.Core;

public sealed record PlaybackEqualizer(
    [property: JsonRequired] float Band1, [property: JsonRequired] float Band2, [property: JsonRequired] float Band3,
    [property: JsonRequired] float Band4, [property: JsonRequired] float Band5, [property: JsonRequired] float Band6,
    [property: JsonRequired] float Band7, [property: JsonRequired] float Band8, [property: JsonRequired] float Band9)
{
    public static PlaybackEqualizer Flat { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public static IReadOnlyList<string> Gsx300BandLabels { get; } = Array.AsReadOnly(new[] { "64 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz" });
    public float[] Levels() => [Band1, Band2, Band3, Band4, Band5, Band6, Band7, Band8, Band9];
    public void Validate()
    {
        if (Levels().Any(v => !float.IsFinite(v) || v is < -6 or > 6)) throw new ArgumentOutOfRangeException(nameof(PlaybackEqualizer), "Playback EQ bands must be -6 to +6 dB.");
    }
    public static PlaybackEqualizer FromLevels(IReadOnlyList<float> levels)
    {
        if (levels.Count != 9) throw new ArgumentException("Playback EQ requires exactly nine bands.", nameof(levels));
        var result = new PlaybackEqualizer(levels[0], levels[1], levels[2], levels[3], levels[4], levels[5], levels[6], levels[7], levels[8]);
        result.Validate(); return result;
    }
}
