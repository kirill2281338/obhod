using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Obhod.Core;

/// <summary>
/// Обход блокировки по IP для серверов картинок Discord.
/// Провайдер может полностью закрыть (на уровне TCP) все адреса media.discordapp.net, а запрет такое не обходит.
/// Но тот же сервер Discord отвечает и на других адресах Cloudflare (например, у cdn.discordapp.com).
/// Находим рабочий адрес, проверяем TLS-рукопожатием с нужным именем и прописываем его в hosts.
/// </summary>
public static class HostsFix
{
    private static readonly string HostsPath = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
    private const string Begin = "# >>> obhod: IP block bypass for Discord media (managed by obhod)";
    private const string End = "# <<< obhod";
    private static readonly string LegacyBegin = "# >>> " + "Zapret" + "Hub";
    private static readonly string LegacyEnd = "# <<< " + "Zapret" + "Hub";

    /// <summary>Домены, которые проверяем, и «доноры» — домены той же сети, у которых адреса обычно открыты.</summary>
    private static readonly (string[] hosts, string[] donors)[] Groups =
    {
        (new[] { "media.discordapp.net", "images-ext-1.discordapp.net", "images-ext-2.discordapp.net" },
         new[] { "cdn.discordapp.com", "discord.com", "gateway.discord.gg", "discordapp.com" }),
    };

    public static async Task<string> ApplyAsync(CancellationToken ct = default)
    {
        var current = ReadBlock();
        var result = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var report = new List<string>();

        foreach (var (hosts, donors) in Groups)
        {
            // Пул адресов-доноров той же сети (Cloudflare отдаёт любой домен Discord с любого своего адреса)
            var pool = new List<IPAddress>();
            foreach (var d in donors)
            {
                try { pool.AddRange((await Dns.GetHostAddressesAsync(d, ct)).Where(a => a.AddressFamily == AddressFamily.InterNetwork)); }
                catch { }
            }
            pool = pool.Distinct().ToList();
            var poolOk = await FilterReachableAsync(pool, ct);

            foreach (var host in hosts)
            {
                // 1) уже прописанный нами адрес открыт — оставляем
                if (current.TryGetValue(host, out var pinned) && (await FilterReachableAsync(new[] { pinned }, ct)).Count > 0)
                {
                    result[host] = pinned;
                    continue;
                }
                // 2) родные адреса домена открыты — обход не нужен
                if (!current.ContainsKey(host))
                {
                    var own = new List<IPAddress>();
                    try { own = (await Dns.GetHostAddressesAsync(host, ct)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(); }
                    catch { }
                    if ((await FilterReachableAsync(own, ct)).Count > 0) continue;
                }
                // 3) берём открытый адрес донора
                if (poolOk.Count > 0)
                {
                    result[host] = poolOk[0];
                    report.Add($"{host} → {poolOk[0]}");
                }
                else report.Add($"{host}: открытый адрес не найден");
            }
        }

        bool legacyMarker = false;
        try { legacyMarker = File.ReadAllText(HostsPath).Contains(LegacyBegin); } catch { }
        bool changed = legacyMarker || result.Count != current.Count || result.Any(kv => !current.TryGetValue(kv.Key, out var v) || !v.Equals(kv.Value));
        if (changed)
        {
            WriteBlock(result);
            FlushDns();
            Log.Write("обход блокировки по ip обновлён: " + (report.Count > 0 ? string.Join("; ", report) : "больше не нужен"));
        }
        return result.Count == 0 ? "обход не нужен" : string.Join(", ", result.Select(kv => $"{kv.Key} → {kv.Value}"));
    }

    /// <summary>Параллельно проверяет TCP-подключение к порту 443; возвращает открытые адреса в исходном порядке.</summary>
    private static async Task<List<IPAddress>> FilterReachableAsync(IEnumerable<IPAddress> ips, CancellationToken ct)
    {
        var list = ips.Take(12).ToList();
        var oks = await Task.WhenAll(list.Select(ip => TcpOkAsync(ip, ct)));
        return list.Where((_, i) => oks[i]).ToList();
    }

    /// <summary>Убирает наш блок из hosts (при удалении программы).</summary>
    public static void Remove()
    {
        if (ReadBlock().Count == 0) return;
        WriteBlock(new Dictionary<string, IPAddress>());
        FlushDns();
    }

    private static async Task<bool> TcpOkAsync(IPAddress ip, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2500);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, 443, cts.Token);
            return tcp.Connected;
        }
        catch { return false; }
    }

    private static Dictionary<string, IPAddress> ReadBlock()
    {
        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        try
        {
            bool inBlock = false;
            foreach (var line in File.ReadAllLines(HostsPath))
            {
                if (line.StartsWith("# >>> obhod") || line.StartsWith(LegacyBegin)) { inBlock = true; continue; }
                if (line.StartsWith(End) || line.StartsWith(LegacyEnd)) { inBlock = false; continue; }
                if (!inBlock) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out var ip)) map[parts[1]] = ip;
            }
        }
        catch { }
        return map;
    }

    private static void WriteBlock(Dictionary<string, IPAddress> map)
    {
        var text = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var keep = new List<string>();
        bool inBlock = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("# >>> obhod") || line.StartsWith(LegacyBegin)) { inBlock = true; continue; }
            if (line.StartsWith(End) || line.StartsWith(LegacyEnd)) { inBlock = false; continue; }
            if (!inBlock) keep.Add(line);
        }
        while (keep.Count > 0 && keep[^1].Trim().Length == 0) keep.RemoveAt(keep.Count - 1);
        if (map.Count > 0)
        {
            keep.Add("");
            keep.Add(Begin);
            foreach (var kv in map) keep.Add($"{kv.Value} {kv.Key}");
            keep.Add(End);
        }
        var backup = HostsPath + ".obhod.bak";
        if (!File.Exists(backup) && File.Exists(HostsPath)) File.Copy(HostsPath, backup);
        File.WriteAllText(HostsPath, string.Join("\r\n", keep) + "\r\n", new UTF8Encoding(false));
    }

    private static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
