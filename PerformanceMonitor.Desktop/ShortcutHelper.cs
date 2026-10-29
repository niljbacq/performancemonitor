using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace TaskManager;

internal static class ShortcutHelper
{
    public const string ShortcutFileName = "Performance Monitor.lnk";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [SupportedOSPlatform("windows")]
    public static void TryCreateStartMenuShortcut()
    {
        try
        {
            // Only run for published exes, not during development in bin/Debug
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || Debugger.IsAttached) return;
            
            string dir = Path.GetDirectoryName(exe) ?? string.Empty;
            if (dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                return;

            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrEmpty(startMenu)) return;

            string lnkPath = Path.Combine(startMenu, ShortcutFileName);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);

            bool targetMatches = File.Exists(lnkPath) &&
                string.Equals((string?)shortcut.TargetPath, exe, StringComparison.OrdinalIgnoreCase);

            if (!targetMatches)
            {
                shortcut.TargetPath = exe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
                shortcut.Description = "Performance Monitor";

                string iconPath = Path.Combine(Path.GetDirectoryName(exe)!, "Assets", "app.ico");
                shortcut.IconLocation = File.Exists(iconPath) ? iconPath : $"{exe},0";
                shortcut.Save();
            }

            // Windows caches Start menu apps; ask it to re-read them on every run.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Best effort: never block app startup if shortcut creation fails
        }
    }
}
