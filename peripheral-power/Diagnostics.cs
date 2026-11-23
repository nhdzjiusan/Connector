using System.Diagnostics;
using System.IO;
using System.Text;

namespace PeripheralPower;

internal static class ErrorLog
{
    private const int Limit = 1024 * 1024;
    private static readonly object Gate = new();
    public static void Write(string operation, Exception error)
    {
        try
        {
            lock (Gate)
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PeripheralPower", "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"error-{DateTime.Now:yyyy-MM-dd}.log");
                string line = $"{DateTimeOffset.Now:O} ERROR {operation}: {error}";
                byte[] bytes = Encoding.UTF8.GetBytes(line[..Math.Min(line.Length, 16384)] + Environment.NewLine);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > Limit)
                    File.Move(path, path + ".1", true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"{operation}: {error.Message}; 日志写入失败: {e.Message}"); }
    }
}

internal sealed record PollResult(BatteryReading[] Fresh, HashSet<string>? Present, bool Partial)
{
    public static PollResult Failed(string source, Exception e)
    {
        ErrorLog.Write(source,e);
        return new([],null,true);
    }
}
