using System;
using System.IO;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace TaskManager;

internal static class ShortcutHelper
{
    public const string ShortcutFileName = "Performance Monitor.lnk";

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
            if (File.Exists(lnkPath)) return; // Idempotent: skip if user already has it

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.Description = "Performance Monitor";
            
            // Set the duck icon! Assumes app.ico is next to the exe (copy it in csproj)
            string iconPath = Path.Combine(Path.GetDirectoryName(exe)!, "Assets", "app.ico");
            shortcut.IconLocation = File.Exists(iconPath) ? iconPath : $"{exe},0";
            
            shortcut.Save();
        }
        catch
        {
            // Best effort: never block app startup if shortcut creation fails
        }
    }
}
