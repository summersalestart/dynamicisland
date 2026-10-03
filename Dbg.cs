using System.IO;

namespace DynIsland;

/// <summary>Tiny capped file log for diagnosing notification capture.
/// %AppData%\DynIsland\notif-debug.log</summary>
public static class Dbg
{
    private static readonly string P = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynIsland", "notif-debug.log");

    public static string LogPath => P;

    public static void Log(string m)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(P)!);
            if (File.Exists(P) && new FileInfo(P).Length > 200 * 1024)
                File.WriteAllText(P, "");
            File.AppendAllText(P, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n");
        }
        catch { }
    }
}
