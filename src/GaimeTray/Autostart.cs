using System;
using System.IO;
using Microsoft.Win32;

namespace GaimeTray
{
    // HKCU Run entry for the tray (main window checkbox, setup wizard).
    static class Autostart
    {
        public static bool IsOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                    return k?.GetValue("GaimeTray") != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        // Point at the windowed apphost exe, NOT dotnet.exe:
                        // dotnet <dll> opens a console window.
                        string exe = Environment.ProcessPath;
                        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            exe = Path.ChangeExtension(exe, ".exe");
                        k.SetValue("GaimeTray", "\"" + exe + "\"");
                    }
                    else k.DeleteValue("GaimeTray", false);
                }
            }
            catch { }
        }
    }
}
