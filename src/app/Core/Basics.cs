using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Obhod.Core;

/// <summary>Все пути программы. Используется ProgramData: путь всегда латиницей (winws не любит кириллицу).</summary>
public static class Paths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obhod");

    /// <summary>Папка данных старой версии (до переименования в «обход»).</summary>
    public static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zapret" + "Hub");

    public static string ZapretDir => Path.Combine(Root, "zapret");
    public static string ZapretBin => Path.Combine(ZapretDir, "bin");
    public static string ZapretLists => Path.Combine(ZapretDir, "lists");
    public static string ZapretUtils => Path.Combine(ZapretDir, "utils");
    public static string Winws => Path.Combine(ZapretBin, "winws.exe");
    public static string TgDir => Path.Combine(Root, "tgws");
    public static string TgExe => Path.Combine(TgDir, "TgWsProxy_windows.exe");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "hub.log");
    public static string TempDir => Path.Combine(Root, "temp");

    public static string TgAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TgWsProxy");
    public static string TgConfig => Path.Combine(TgAppData, "config.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TempDir);
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static event Action<string>? Line;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message.ToLowerInvariant()}";
        lock (Gate)
        {
            try
            {
                Paths.Ensure();
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* лог не должен ронять программу */ }
        }
        Line?.Invoke(line);
    }
}

public sealed class CustomService
{
    public string Name { get; set; } = "";
    public List<string> Domains { get; set; } = new();
}

public sealed class AppSettings
{
    public string? LastGoodStrategy { get; set; }
    public string? SelectedStrategy { get; set; }
    public Dictionary<string, int> Scores { get; set; } = new();
    public DateTime? LastAutoPick { get; set; }
    public Dictionary<string, string> NetworkStrategies { get; set; } = new();

    public HashSet<string> EnabledServices { get; set; } = new() { "discord", "youtube", "cloudflare" };
    public List<CustomService> CustomServices { get; set; } = new();

    public bool AutoStartZapret { get; set; } = true;
    public bool AutoPickOnStart { get; set; } = true;
    public bool AutoStartTg { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;

    /// <summary>disabled | all | tcp | udp</summary>
    public string GameFilter { get; set; } = "disabled";
    public string? TgVersion { get; set; }
    public VpnSettings Vpn { get; set; } = new();
    /// <summary>graphite | black</summary>
    public string Theme { get; set; } = "black";
    /// <summary>Один раз переключить vpn на «авто» после обновления (надёжнее одиночного сервера).</summary>
    public bool VpnAutoSet { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return Migrate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json) ?? new AppSettings());
        }
        catch (Exception ex) { Log.Write("настройки повреждены, сброшены: " + ex.Message); }
        return new AppSettings();
    }

    /// <summary>Старые авторские стратегии назывались «Hub · …», теперь «DAVL · …».</summary>
    private static AppSettings Migrate(AppSettings s)
    {
        static string? Fix(string? n) => n != null && n.StartsWith("Hub · ") ? "DAVL · " + n[6..] : n;
        s.LastGoodStrategy = Fix(s.LastGoodStrategy);
        s.SelectedStrategy = Fix(s.SelectedStrategy);
        s.Scores = s.Scores.ToDictionary(kv => Fix(kv.Key)!, kv => kv.Value);
        return s;
    }

    public void Save()
    {
        try
        {
            Paths.Ensure();
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), Encoding.UTF8);
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Write("не удалось сохранить настройки: " + ex.Message); }
    }
}
