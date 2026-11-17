using System.Diagnostics;
using Microsoft.Win32;

namespace WindowsNotch.Services;

public static class PowerService
{
    public static (int percent, bool charging, bool hasBattery) Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return (100, true, false);
        var has = s.BatteryFlag != 255 && (s.BatteryFlag & 128) == 0;
        var pct = s.BatteryLifePercent > 100 ? 100 : s.BatteryLifePercent;
        return (pct, s.ACLineStatus == 1, has);
    }

    /// <summary>Estimated battery time left in seconds, or -1 when Windows hasn't estimated it yet.</summary>
    public static int SecondsLeft() => Native.GetSystemPowerStatus(out var s) ? s.BatteryLifeTime : -1;

    public static void Lock() => Native.LockWorkStation();
    public static void Sleep() => Native.SetSuspendState(false, false, false);
    public static void Restart() => Run("shutdown", "/r /t 0");
    public static void Shutdown() => Run("shutdown", "/s /t 0");

    private static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("WindowsNotch") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("WindowsNotch", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("WindowsNotch", false);
        }
    }
}
