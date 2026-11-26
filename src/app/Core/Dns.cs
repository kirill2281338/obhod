using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Obhod.Core;

public sealed record DnsProvider(string Name, string Hint, string Primary, string Secondary, string? Doh);

/// <summary>Популярные DNS-серверы: пинг, применение к активным сетевым адаптерам и возврат к DNS провайдера.</summary>
public static class DnsManager
{
    public static readonly IReadOnlyList<DnsProvider> Providers = new[]
    {
        new DnsProvider("cloudflare", "быстрый, без логов", "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query"),
        new DnsProvider("google", "надёжный, по всему миру", "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query"),
        new DnsProvider("quad9", "блокирует вредоносные сайты", "9.9.9.9", "149.112.112.112", "https://dns.quad9.net/dns-query"),
        new DnsProvider("adguard", "блокирует рекламу и трекеры", "94.140.14.14", "94.140.15.15", "https://dns.adguard-dns.com/dns-query"),
        new DnsProvider("comss", "открывает сервисы с геоблоком (chatgpt и др.)", "83.220.169.155", "212.109.195.93", "https://dns.comss.one/dns-query"),
        new DnsProvider("яндекс", "серверы в россии", "77.88.8.8", "77.88.8.1", null),
        new DnsProvider("opendns", "от cisco", "208.67.222.222", "208.67.220.220", "https://doh.opendns.com/dns-query"),
        new DnsProvider("control d", "бесплатный, без фильтров", "76.76.2.0", "76.76.10.0", "https://freedns.controld.com/p0"),
    };

    /// <summary>Средний пинг из трёх попыток, -1 если не отвечает.</summary>
    public static async Task<int> PingAsync(string ip)
    {
        var times = new List<long>();
        using var ping = new Ping();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var r = await ping.SendPingAsync(IPAddress.Parse(ip), 1500);
                if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
            }
            catch { }
        }
        return times.Count == 0 ? -1 : (int)times.Average();
    }

    /// <summary>DNS-серверы, которые сейчас стоят на активных адаптерах (IPv4).</summary>
    public static List<string> Current()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.GetIPProperties().GatewayAddresses.Any())
                .SelectMany(n => n.GetIPProperties().DnsAddresses)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Distinct().ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Ставит DNS на все активные адаптеры и включает DoH (Windows 11), если сервер его поддерживает.</summary>
    public static async Task<string?> ApplyAsync(DnsProvider p)
    {
        var doh = p.Doh == null ? "" :
            $"foreach ($ip in '{p.Primary}','{p.Secondary}') {{ try {{ Add-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate '{p.Doh}' -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }} catch {{ try {{ Set-DnsClientDohServerAddress -ServerAddress $ip -DohTemplate '{p.Doh}' -AllowFallbackToUdp $true -AutoUpgrade $true -ErrorAction Stop }} catch {{}} }} }}; ";
        var script = doh + $"Get-NetAdapter | Where-Object {{ $_.Status -eq 'Up' }} | Set-DnsClientServerAddress -ServerAddresses '{p.Primary}','{p.Secondary}'; Clear-DnsClientCache";
        var err = await RunPs(script);
        Log.Write(err == null ? $"dns: выбран {p.Name} ({p.Primary}, {p.Secondary})" : $"dns: ошибка — {err}");
        return err;
    }

    public static async Task<string?> ResetAsync()
    {
        var err = await RunPs("Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | Set-DnsClientServerAddress -ResetServerAddresses; Clear-DnsClientCache");
        Log.Write(err == null ? "dns: возвращены серверы провайдера" : $"dns: ошибка — {err}");
        return err;
    }

    private static async Task<string?> RunPs(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var err = (await errTask).Trim();
            return p.ExitCode == 0 && err.Length == 0 ? null : (err.Length > 0 ? err.Split('\n')[0] : $"код {p.ExitCode}");
        }
        catch (Exception ex) { return ex.Message; }
    }
}
