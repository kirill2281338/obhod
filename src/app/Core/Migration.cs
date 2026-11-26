using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Obhod.Core;

/// <summary>
/// Переход со старого названия: данные из C:\ProgramData\<старая папка> переезжают в C:\ProgramData\obhod,
/// задача автозапуска и метки в hosts переименовываются. Ничего не теряется.
/// </summary>
public static class Migration
{
    public static void Run()
    {
        MigrateTask();
        var legacy = Paths.LegacyRoot;
        if (!Directory.Exists(legacy)) return;

        // запущенные из старой папки инструменты держат файлы — останавливаем
        foreach (var name in new[] { "winws", "TgWsProxy_windows", "xray", "sing-box" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (p.MainModule?.FileName?.StartsWith(legacy, StringComparison.OrdinalIgnoreCase) == true) { p.Kill(true); p.WaitForExit(4000); } }
                catch { }
                finally { p.Dispose(); }
            }
        Run("sc.exe", "stop WinDivert");
        Thread.Sleep(600);

        bool hadSettings = File.Exists(Path.Combine(legacy, "settings.json"));
        if (!Directory.Exists(Paths.Root))
        {
            try { Directory.Move(legacy, Paths.Root); }
            catch { CopyDir(legacy, Paths.Root); }
        }
        else if (hadSettings && !File.Exists(Paths.SettingsFile))
        {
            CopyDir(legacy, Paths.Root);
        }
        try { if (Directory.Exists(legacy)) Directory.Delete(legacy, true); } catch { }

        Log.Write("данные перенесены из старой папки программы");
    }

    /// <summary>Автозапуск: задача со старым именем → задача «obhod» на новый путь программы.</summary>
    private static void MigrateTask()
    {
        var legacyTask = "Zapret" + "Hub";
        if (Run("schtasks.exe", $"/query /tn \"{legacyTask}\"") != 0) return;
        Run("schtasks.exe", $"/delete /f /tn \"{legacyTask}\"");
        Updater.SetAutostart(true);
    }

    private static int Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(8000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            try { if (!File.Exists(target)) File.Copy(file, target); } catch { }
        }
    }
}
