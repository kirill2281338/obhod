using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Obhod.Core;

public static class ZapretEngine
{
    private static Process? _proc;
    public static string? RunningStrategy { get; private set; }

    public static bool IsRunning
    {
        get
        {
            var ps = Process.GetProcessesByName("winws");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
    }

    public static bool IsInstalled => File.Exists(Paths.Winws);

    public static async Task StopAsync()
    {
        var p = _proc;
        _proc = null;
        RunningStrategy = null;
        if (p != null)
        {
            try
            {
                p.Kill(true);
                using var cts = new CancellationTokenSource(4000);
                await p.WaitForExitAsync(cts.Token);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Запускает winws с параметрами стратегии. Возвращает текст ошибки или null при успехе.</summary>
    public static async Task<string?> StartAsync(Strategy s, AppSettings settings)
    {
        if (!IsInstalled) return "не найден winws.exe. обновите запрет во вкладке «обновления».";
        await StopAsync();
        if (IsRunning) return "winws запущен вне программы. остановите его перед запуском запрета здесь.";
        ServiceCatalog.WriteUserList(settings);

        var args = StrategyLoader.Expand(s.ArgsTemplate, settings);
        var psi = new ProcessStartInfo(Paths.Winws, args)
        {
            WorkingDirectory = Paths.ZapretBin,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var tail = new LinkedList<string>();
        void Collect(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail) { tail.AddLast(line); if (tail.Count > 12) tail.RemoveFirst(); }
        }

        Process p;
        try
        {
            p = Process.Start(psi)!;
        }
        catch (Exception ex) { return "не удалось запустить winws.exe: " + ex.Message; }

        p.OutputDataReceived += (_, e) => Collect(e.Data);
        p.ErrorDataReceived += (_, e) => Collect(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // winws падает сразу, если параметры неверные или WinDivert занят. Ждём немного и проверяем.
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(900));
        if (p.HasExited)
        {
            await Task.Delay(50);
            string text;
            lock (tail) text = string.Join(" | ", tail.TakeLast(4));
            var code = SafeExit(p);
            p.Dispose();
            return $"winws завершился (код {code}): {text}";
        }
        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _proc = p;
        RunningStrategy = s.Name;
        return null;
    }

    private static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    public static string? LocalVersion()
    {
        try
        {
            var svc = Path.Combine(Paths.ZapretDir, "service.bat");
            if (!File.Exists(svc)) return null;
            var m = Regex.Match(File.ReadAllText(svc), "LOCAL_VERSION=([0-9][0-9A-Za-z.\\-]*)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }
}

public static class TgProxy
{
    private const string ProcName = "TgWsProxy_windows";

    public static bool IsInstalled => File.Exists(Paths.TgExe);

    public static bool IsRunning
    {
        get
        {
            var ps = Process.GetProcessesByName(ProcName);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
    }

    public static (string host, int port, string? secret) ReadConfig()
    {
        try
        {
            if (File.Exists(Paths.TgConfig))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.TgConfig));
                var r = doc.RootElement;
                var host = r.TryGetProperty("host", out var h) ? h.GetString() ?? "127.0.0.1" : "127.0.0.1";
                var port = r.TryGetProperty("port", out var p) && p.TryGetInt32(out var pi) ? pi : 1443;
                var secret = r.TryGetProperty("secret", out var s) ? s.GetString() : null;
                return (host, port, secret);
            }
        }
        catch { }
        return ("127.0.0.1", 1443, null);
    }

    public static string? TelegramLink()
    {
        var (host, port, secret) = ReadConfig();
        if (string.IsNullOrEmpty(secret)) return null;
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    public static async Task<bool> IsListeningAsync()
    {
        var (host, port, _) = ReadConfig();
        try
        {
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(400);
            await c.ConnectAsync(host == "0.0.0.0" ? "127.0.0.1" : host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public static async Task<string?> StartAsync()
    {
        if (!IsInstalled) return "не найден tgwsproxy. скачайте его во вкладке «обновления».";
        if (IsRunning) return null;
        try
        {
            Process.Start(new ProcessStartInfo(Paths.TgExe) { UseShellExecute = true, WorkingDirectory = Paths.TgDir })?.Dispose();
        }
        catch (Exception ex) { return "не удалось запустить прокси: " + ex.Message; }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(200);
            if (await IsListeningAsync()) return null;
        }
        return IsRunning ? null : "прокси закрылся сразу после запуска. смотрите %appdata%\\tgwsproxy\\proxy.log";
    }

    public static async Task StopAsync()
    {
        foreach (var p in Process.GetProcessesByName(ProcName))
        {
            try
            {
                p.Kill(true);
                using var cts = new CancellationTokenSource(4000);
                await p.WaitForExitAsync(cts.Token);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Версия из лога самого прокси («версия 1.12.0 starting»).</summary>
    public static string? VersionFromLog()
    {
        try
        {
            var vf = Path.Combine(Paths.TgDir, "version.txt");
            if (File.Exists(vf)) return File.ReadAllText(vf).Trim().TrimStart('v');
            var log = Path.Combine(Paths.TgAppData, "proxy.log");
            if (!File.Exists(log)) return null;
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var text = sr.ReadToEnd();
            var ms = Regex.Matches(text, @"(?:версия|version)\s+v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            return ms.Count > 0 ? ms[^1].Groups[1].Value : null;
        }
        catch { return null; }
    }
}
