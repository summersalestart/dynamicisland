using Microsoft.Win32;

namespace DynIsland;

public static class StartupHelper
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "DynIsland";

    public static void SetEnabled(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key, true);
            if (k == null) return;
            if (on)
            {
                string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exe)) k.SetValue(Name, $"\"{exe}\" --hidden");
            }
            else k.DeleteValue(Name, false);
        }
        catch { }
    }
}
