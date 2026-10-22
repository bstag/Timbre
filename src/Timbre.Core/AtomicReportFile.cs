using System.Text;

namespace Timbre.Core;

// Reports are replaced frequently while other processes inspect them. Readers
// must permit deletion; bounded retries cover older readers and transient locks.
public static class AtomicReportFile
{
    public static void Write(string path, string text) => Write(path, text, null);
    internal static void Write(string path, string text, Action<int>? onContention)
    {
        if (Directory.Exists(path)) throw new IOException("Report path is a directory.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            for (var attempt = 0; ; attempt++) {
                try {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    break;
                }
                catch (Exception ex) when (attempt < 20 && (ex is IOException or UnauthorizedAccessException) && IsContention(ex)) {
                    onContention?.Invoke(attempt + 1); Thread.Sleep(25);
                }
            }
        } finally {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static bool IsContention(Exception error) => (error.HResult & 0xffff) is 5 or 32 or 33;
    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    public static byte[] Read(string path, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var stream = OpenRead(path);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Report exceeds its size limit.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Report size changed during reading.");
        return bytes;
    }
}
