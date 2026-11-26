using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Obhod.Core;

public sealed class Strategy
{
    public required string Name { get; init; }
    /// <summary>Аргументы winws в стиле батника: с %BIN%, %LISTS%, %GameFilterTCP%…</summary>
    public required string ArgsTemplate { get; init; }
    public bool IsHub { get; init; }
    public string Display => Name.ToLowerInvariant();
    public override string ToString() => Display;
}

public static class StrategyLoader
{
    /// <summary>Читает все general*/test*.bat из папки запрета и вытаскивает из них параметры winws.</summary>
    public static List<Strategy> Load(string zapretDir)
    {
        var list = new List<Strategy>();
        if (Directory.Exists(zapretDir))
        {
            foreach (var file in Directory.GetFiles(zapretDir, "*.bat"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith("service", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var args = ParseBat(File.ReadAllText(file, Encoding.UTF8));
                    if (args != null) list.Add(new Strategy { Name = name, ArgsTemplate = args });
                }
                catch (Exception ex) { Log.Write($"не разобрал {name}: {ex.Message}"); }
            }
        }
        list.Sort((a, b) => NaturalKey(a.Name).CompareTo(NaturalKey(b.Name)));
        list.AddRange(HubStrategies.All);
        return list;
    }

    private static string NaturalKey(string n)
    {
        // general -> 0, general (ALT) -> 1.., test -> 2..; числа дополняются нулями для правильной сортировки
        var group = n.StartsWith("general", StringComparison.OrdinalIgnoreCase) ? (n.Equals("general", StringComparison.OrdinalIgnoreCase) ? "0" : "1") : "2";
        return group + Regex.Replace(n.ToLowerInvariant(), @"\d+", m => m.Value.PadLeft(4, '0'));
    }

    public static string? ParseBat(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimStart();
            if (!l.StartsWith("start ", StringComparison.OrdinalIgnoreCase) ||
                l.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var sb = new StringBuilder();
            int j = i;
            while (j < lines.Length)
            {
                var cur = lines[j].TrimEnd();
                bool cont = cur.EndsWith("^");
                if (cont) cur = cur[..^1];
                sb.Append(cur).Append(' ');
                if (!cont) break;
                j++;
            }
            var full = sb.ToString();
            var idx = full.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var args = full[(idx + "winws.exe\"".Length)..].Trim();
            args = Regex.Replace(args, @"\s+", " ");
            args = Regex.Replace(args, @"\^(.)", "$1"); // экранирование cmd: ^! -> !
            return args.Length > 0 ? args : null;
        }
        return null;
    }

    public static string Expand(string template, AppSettings s)
    {
        var (tcp, udp) = GameFilterPorts(s.GameFilter);
        var bin = Paths.ZapretBin + "\\";
        var lists = Paths.ZapretLists + "\\";
        return template
            .Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", tcp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", udp, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilter%", tcp != "12" ? tcp : udp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Как в service.bat: выключенный фильтр = фиктивный порт 12.</summary>
    public static (string tcp, string udp) GameFilterPorts(string mode) => mode switch
    {
        "all" => ("1024-65535", "1024-65535"),
        "tcp" => ("1024-65535", "12"),
        "udp" => ("12", "1024-65535"),
        _ => ("12", "12"),
    };
}

/// <summary>
/// Дополнительные стратегии Hub: та же структура фильтров, что у general.bat,
/// но другие приёмы обхода для TCP (TLS). Авто-подбор сам проверит, работают ли они у провайдера.
/// </summary>
public static class HubStrategies
{
    private const string Wf = "--wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ";
    private const string Lists = "--hostlist=\"%LISTS%list-general.txt\" --hostlist=\"%LISTS%list-general-user.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\" --hostlist-exclude=\"%LISTS%list-exclude-user.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\" --ipset-exclude=\"%LISTS%ipset-exclude-user.txt\"";
    private const string IpAll = "--ipset=\"%LISTS%ipset-all.txt\" --hostlist-exclude=\"%LISTS%list-exclude.txt\" --hostlist-exclude=\"%LISTS%list-exclude-user.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\" --ipset-exclude=\"%LISTS%ipset-exclude-user.txt\"";
    private const string Quic = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=\"%BIN%quic_initial_www_google_com.bin\"";
    private const string GameIp = "--ipset=\"%LISTS%ipset-all.txt\" --ipset-exclude=\"%LISTS%ipset-exclude.txt\" --ipset-exclude=\"%LISTS%ipset-exclude-user.txt\"";

    private static string Build(string tcp, string google) =>
        Wf +
        $"--filter-udp=443 {Lists} {Quic} --new " +
        "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-fake-discord=\"%BIN%ACTIVE_DISCORD_UDP.bin\" --dpi-desync-fake-stun=\"%BIN%ACTIVE_DISCORD_UDP.bin\" --dpi-desync-repeats=6 --new " +
        $"--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media {tcp} --new " +
        $"--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --ip-id=zero {google} --new " +
        $"--filter-tcp=80,443 {Lists} {tcp} --new " +
        $"--filter-udp=443 {IpAll} {Quic} --new " +
        $"--filter-tcp=80,443,8443 {IpAll} {tcp} --new " +
        $"--filter-tcp=%GameFilterTCP% {GameIp} {tcp} --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3 --new " +
        $"--filter-udp=%GameFilterUDP% {GameIp} --dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp=\"%BIN%ACTIVE_GAME_UDP.bin\" --dpi-desync-cutoff=n2";

    private const string GoogleSeqovl = "--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=\"%BIN%tls_clienthello_www_google_com.bin\"";

    public static readonly IReadOnlyList<Strategy> All = new[]
    {
        new Strategy
        {
            Name = "DAVL · fake + multisplit (badseq)", IsHub = true,
            ArgsTemplate = Build(
                "--dpi-desync=fake,multisplit --dpi-desync-split-pos=1,sniext+1 --dpi-desync-split-seqovl=1 --dpi-desync-fooling=badseq --dpi-desync-repeats=6 --dpi-desync-fake-tls=\"%BIN%tls_clienthello_www_google_com.bin\" --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
                GoogleSeqovl),
        },
        new Strategy
        {
            Name = "DAVL · fakedsplit midsld (ts)", IsHub = true,
            ArgsTemplate = Build(
                "--dpi-desync=fake,fakedsplit --dpi-desync-split-pos=midsld --dpi-desync-fooling=ts --dpi-desync-repeats=6 --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=\"%BIN%tls_clienthello_www_google_com.bin\"",
                "--dpi-desync=fake,fakedsplit --dpi-desync-split-pos=midsld --dpi-desync-fooling=ts --dpi-desync-repeats=6 --dpi-desync-fake-tls=\"%BIN%tls_clienthello_www_google_com.bin\""),
        },
        new Strategy
        {
            Name = "DAVL · hostfakesplit (google)", IsHub = true,
            ArgsTemplate = Build(
                "--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1 --dpi-desync-fooling=ts --dpi-desync-repeats=4",
                GoogleSeqovl),
        },
        new Strategy
        {
            Name = "DAVL · seqovl 4pda + autottl", IsHub = true,
            ArgsTemplate = Build(
                "--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=\"%BIN%tls_clienthello_4pda_to.bin\" --dpi-desync-autottl=2 --dpi-desync-repeats=4 --dpi-desync-fake-tls=\"%BIN%tls_clienthello_4pda_to.bin\"",
                GoogleSeqovl),
        },
    };
}
