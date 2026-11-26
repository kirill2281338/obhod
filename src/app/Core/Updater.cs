using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Obhod.Core;

public sealed record ReleaseInfo(string Version, string? AssetUrl, string? PageUrl);

public static class Updater
{
    public const string ZapretRepo = "Flowseal/zapret-discord-youtube";
    public const string TgRepo = "Flowseal/tg-ws-proxy";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = false, // системный прокси может указывать на выключенный vpn
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("obhod/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static async Task<T> Retry<T>(Func<Task<T>> f, int tries = 3)
    {
        for (int i = 1; ; i++)
        {
            try { return await f(); }
            catch when (i < tries) { await Task.Delay(800 * i); }
        }
    }

    private static async Task<ReleaseInfo?> LatestFromApi(string repo, Func<string, bool> assetFilter, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", cts.Token);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        string? asset = null;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (assetFilter(name)) { asset = a.GetProperty("browser_download_url").GetString(); break; }
            }
        var page = r.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
        return new ReleaseInfo(tag.TrimStart('v', 'V'), asset, page);
    }

    public static async Task<ReleaseInfo?> LatestZapretAsync(CancellationToken ct = default)
    {
        try
        {
            return await Retry(() => LatestFromApi(ZapretRepo, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), ct));
        }
        catch (Exception ex)
        {
            Log.Write("github api (запрет) недоступен, пробую version.txt: " + ex.Message);
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var v = (await Http.GetStringAsync($"https://raw.githubusercontent.com/{ZapretRepo}/main/.service/version.txt", cts.Token)).Trim();
            return new ReleaseInfo(v, $"https://github.com/{ZapretRepo}/releases/download/{v}/zapret-discord-youtube-{v}.zip",
                $"https://github.com/{ZapretRepo}/releases/tag/{v}");
        }
        catch (Exception ex)
        {
            Log.Write("не удалось узнать версию запрета: " + ex.Message);
            return null;
        }
    }

    public static async Task<ReleaseInfo?> LatestTgAsync(CancellationToken ct = default)
    {
        try
        {
            return await Retry(() => LatestFromApi(TgRepo,
                n => n.Equals("TgWsProxy_windows.exe", StringComparison.OrdinalIgnoreCase) ||
                     (n.Contains("windows", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)), ct));
        }
        catch (Exception ex)
        {
            Log.Write("не удалось узнать версию tgwsproxy: " + ex.Message);
            return null;
        }
    }

    public static bool IsNewer(string? remote, string? local)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        if (string.IsNullOrWhiteSpace(local)) return true;
        static Version? P(string s)
        {
            var clean = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
            if (!clean.Contains('.')) clean += ".0";
            return Version.TryParse(clean, out var v) ? v : null;
        }
        var r = P(remote); var l = P(local);
        if (r != null && l != null) return r > l;
        return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Download(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = File.Create(dest))
        {
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (new FileInfo(dest).Length < 1024) throw new IOException("скачанный файл подозрительно мал.");
    }

    /// <summary>Пользовательские файлы, которые сохраняются при обновлении запрета.</summary>
    private static readonly string[] KeepFiles =
    {
        @"lists\list-general-user.txt", @"lists\list-exclude-user.txt", @"lists\ipset-exclude-user.txt",
        @"utils\game_filter.enabled", @"utils\check_updates.enabled",
    };

    public static async Task InstallZapretAsync(ReleaseInfo rel, IProgress<double>? progress, CancellationToken ct)
    {
        if (rel.AssetUrl == null) throw new InvalidOperationException("в релизе нет zip-архива.");
        Paths.Ensure();
        var zip = Path.Combine(Paths.TempDir, $"zapret-{rel.Version}.zip");
        var unpack = Path.Combine(Paths.TempDir, "zapret-unpack");
        await Retry(async () => { await Download(rel.AssetUrl, zip, progress, ct); return true; });

        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
        ZipFile.ExtractToDirectory(zip, unpack);
        var root = FindZapretRoot(unpack) ?? throw new InvalidDataException("в архиве не найден bin\\winws.exe.");

        var saved = new Dictionary<string, byte[]>();
        foreach (var f in KeepFiles)
        {
            var p = Path.Combine(Paths.ZapretDir, f);
            if (File.Exists(p)) saved[f] = await File.ReadAllBytesAsync(p, ct);
        }

        await ZapretEngine.StopAsync();
        await Task.Delay(300, ct);
        var old = Paths.ZapretDir + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(Paths.ZapretDir)) Directory.Move(Paths.ZapretDir, old);
        try
        {
            Directory.Move(root, Paths.ZapretDir);
        }
        catch
        {
            if (Directory.Exists(old) && !Directory.Exists(Paths.ZapretDir)) Directory.Move(old, Paths.ZapretDir);
            throw;
        }
        foreach (var (f, bytes) in saved)
        {
            var p = Path.Combine(Paths.ZapretDir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllBytesAsync(p, bytes, ct);
        }
        TryDelete(old); TryDelete(unpack); TryDeleteFile(zip);
        Log.Write($"запрет обновлён до {rel.Version}");
    }

    public static async Task InstallTgAsync(ReleaseInfo rel, IProgress<double>? progress, CancellationToken ct)
    {
        if (rel.AssetUrl == null) throw new InvalidOperationException("в релизе нет tgwsproxy_windows.exe.");
        Paths.Ensure();
        Directory.CreateDirectory(Paths.TgDir);
        var tmp = Path.Combine(Paths.TempDir, "TgWsProxy_windows.exe.download");
        await Retry(async () => { await Download(rel.AssetUrl, tmp, progress, ct); return true; });
        bool wasRunning = TgProxy.IsRunning;
        await TgProxy.StopAsync();
        await Task.Delay(300, ct);
        File.Move(tmp, Paths.TgExe, true);
        try { File.WriteAllText(Path.Combine(Paths.TgDir, "version.txt"), rel.Version); } catch { }
        Log.Write($"tgwsproxy обновлён до {rel.Version}");
        if (wasRunning) await TgProxy.StartAsync();
    }

    private static string? FindZapretRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "bin", "winws.exe"))) return dir;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var r = FindZapretRoot(d);
            if (r != null) return r;
        }
        return null;
    }

    private static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    private static void TryDeleteFile(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    // ---------- Первый запуск: берём уже скачанные копии, исходные папки не меняем ----------

    public static bool ImportLocalZapret()
    {
        if (ZapretEngine.IsInstalled) return true;
        foreach (var src in ZapretCandidates())
        {
            try
            {
                CopyDir(src, Paths.ZapretDir);
                Log.Write($"скопирован запрет из {src}");
                return true;
            }
            catch (Exception ex) { Log.Write($"не удалось скопировать {src}: {ex.Message}"); }
        }
        return false;
    }

    public static bool ImportLocalTg(AppSettings s)
    {
        if (TgProxy.IsInstalled) return true;
        foreach (var f in TgCandidates())
        {
            try
            {
                Directory.CreateDirectory(Paths.TgDir);
                File.Copy(f, Paths.TgExe, true);
                s.TgVersion ??= TgProxy.VersionFromLog();
                Log.Write($"скопирован tgwsproxy из {f}");
                return true;
            }
            catch (Exception ex) { Log.Write($"не удалось скопировать {f}: {ex.Message}"); }
        }
        return false;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            AppContext.BaseDirectory,
        };
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\'));
        for (int i = 0; i < 5 && parent != null; i++, parent = parent.Parent) list.Add(parent.FullName);
        return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ZapretCandidates()
    {
        foreach (var root in SearchRoots())
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(root, "*zapret*"); } catch { continue; }
            foreach (var d in dirs.OrderByDescending(x => x))
                if (File.Exists(Path.Combine(d, "bin", "winws.exe")) && File.Exists(Path.Combine(d, "service.bat")))
                    yield return d;
        }
    }

    private static IEnumerable<string> TgCandidates()
    {
        foreach (var root in SearchRoots())
        {
            IEnumerable<string> files;
            try { files = Directory.GetFiles(root, "TgWsProxy*.exe"); } catch { continue; }
            foreach (var f in files) yield return f;
        }
        // уже запущенный экземпляр
        foreach (var p in Process.GetProcessesByName("TgWsProxy_windows"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            p.Dispose();
            if (path != null) yield return path;
        }
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains("test results", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        }
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.Contains("test results", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    // ---------- Автозапуск с Windows (через планировщик, чтобы стартовать с правами администратора) ----------

    public static bool SetAutostart(bool on)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "obhod.exe");
            var args = on
                ? $"/create /f /tn \"obhod\" /sc onlogon /rl highest /delay 0000:10 /tr \"\\\"{exe}\\\" --tray\""
                : "/delete /f /tn \"obhod\"";
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Write("автозапуск: " + ex.Message); return false; }
    }

    public static bool IsAutostartOn()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/query /tn \"obhod\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
