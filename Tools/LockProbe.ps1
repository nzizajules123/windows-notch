# Diagnostic: prints what the notch's lock detection sees (WTS session flags + LogonUI).
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class P {
  [DllImport("wtsapi32.dll")] public static extern bool WTSQuerySessionInformation(IntPtr s, int id, int c, out IntPtr b, out int n);
  [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
  public static int Flags(int id){ IntPtr b; int n; if(!WTSQuerySessionInformation(IntPtr.Zero,id,25,out b,out n)) return -99; try { return Marshal.ReadInt32(b,12);} finally { WTSFreeMemory(b);} }
}
"@
$sid = (Get-Process -Id $PID).SessionId
"session $sid flags = $([P]::Flags($sid))  (0 = locked, 1 = unlocked)"
"LogonUI running = $([bool](Get-Process LogonUI -ErrorAction SilentlyContinue))"
