using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace Obhod.Core;

public sealed class CheckResult
{
    public required string Service { get; init; }
    public required string Url { get; init; }
    public bool Tls12 { get; set; }
    public bool Tls13 { get; set; }
    /// <summary>Скачались ли первые 64 КБ без «заморозки» на 16–20 КБ (типичный признак ТСПУ).</summary>
    public bool Stream { get; set; }
    public long Ms { get; set; }
    public string? Error { get; set; }
    public bool Ok => Tls12 && Tls13 && Stream;
    /// <summary>Сервер не отвечает даже на TCP: это блокировка по IP, запрет её не обходит (нужен VPN/прокси). В баллы не входит.</summary>
    public bool IpBlocked { get; set; }
}

public sealed class TestReport
{
    public List<CheckResult> Checks { get; } = new();
    public int Passed => Checks.Where(c => !c.IpBlocked).Sum(c => (c.Tls12 ? 1 : 0) + (c.Tls13 ? 1 : 0) + (c.Stream ? 1 : 0));
    public int Total => Checks.Count(c => !c.IpBlocked) * 3;
    public double Ratio => Total == 0 ? 0 : (double)Passed / Total;
    public long AvgMs => Checks.Count(c => c.Ms > 0) is var n and > 0 ? Checks.Where(c => c.Ms > 0).Sum(c => c.Ms) / n : 0;
    public bool Perfect => Total > 0 && Passed == Total;
    public string? StartError { get; set; }
    public int Score => Total == 0 ? 0 : (int)Math.Round(1000.0 * Passed / Total);
}

public static class Tester
{
    private const int StreamBytes = 64 * 1024;
    private const string NoTcp = "нет tcp-соединения (блок по ip?)";

    private static HttpMessageInvoker MakeClient(SslProtocols proto, int timeoutSec)
    {
        var h = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(4, timeoutSec)),
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = 4,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = proto },
            ConnectCallback = HappyEyeballsAsync,
        };
        return new HttpMessageInvoker(h, true);
    }

    /// <summary>
    /// Подключение как в браузере: пробуем несколько IP домена параллельно (с шагом 200 мс) и берём первый ответивший.
    /// Провайдеры часто блокируют по IP только часть адресов (например, у cdn.discordapp.com),
    /// а Discord и браузер просто уходят на соседний адрес. Без этого тест ложно считал Discord недоступным.
    /// </summary>
    private static async ValueTask<System.IO.Stream> HappyEyeballsAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;
        var all = await Dns.GetHostAddressesAsync(host, ct);
        var ordered = all.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                         .Concat(all.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6))
                         .Take(6).ToArray();
        if (ordered.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        for (int i = 0; i < ordered.Length; i++)
            pending.Add(TryConnect(ordered[i], port, TimeSpan.FromMilliseconds(150 * i), race.Token));

        Exception? last = null;
        while (pending.Count > 0)
        {
            var t = await Task.WhenAny(pending);
            pending.Remove(t);
            if (t.IsCompletedSuccessfully)
            {
                race.Cancel();
                foreach (var p in pending)
                    _ = p.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); }, TaskScheduler.Default);
                return new NetworkStream(t.Result, ownsSocket: true);
            }
            last = t.Exception?.InnerException ?? last;
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.TimedOut);
    }

    private static async Task<Socket> TryConnect(IPAddress addr, int port, TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(addr, port, ct);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        r.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
        r.Headers.TryAddWithoutValidation("Accept", "*/*");
        return r;
    }

    public static async Task<TestReport> RunAsync(IReadOnlyList<(string service, string url)> targets, int timeoutSec, CancellationToken ct)
    {
        var report = new TestReport();
        using var c12 = MakeClient(SslProtocols.Tls12, timeoutSec);
        using var c13 = MakeClient(SslProtocols.Tls13, timeoutSec);

        var tasks = targets.Select(async t =>
        {
            var cr = new CheckResult { Service = t.service, Url = t.url };
            var a = Head(c12, t.url, timeoutSec, ct);
            var b = Stream(c13, t.url, timeoutSec, ct);
            var (ok12, ms12, err12) = await a;
            var (ok13, okStream, ms13, err13) = await b;
            // Сбой на этапе TCP (не DPI) бывает случайным — например, сразу после перезапуска winws. Повторяем один раз.
            if (!ok12 && err12 == NoTcp) (ok12, ms12, err12) = await Head(c12, t.url, timeoutSec, ct);
            if (!ok13 && err13 == NoTcp) (ok13, okStream, ms13, err13) = await Stream(c13, t.url, timeoutSec, ct);
            cr.Tls12 = ok12;
            cr.Tls13 = ok13;
            cr.Stream = okStream;
            cr.Ms = ok13 ? ms13 : ok12 ? ms12 : 0;
            cr.Error = err13 ?? err12;
            cr.IpBlocked = !ok12 && !ok13 && err12 == NoTcp && err13 == NoTcp;
            return cr;
        }).ToList();

        foreach (var r in await Task.WhenAll(tasks)) report.Checks.Add(r);
        return report;
    }

    private static async Task<(bool ok, long ms, string? err)> Head(HttpMessageInvoker c, string url, int timeoutSec, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await c.SendAsync(Req(HttpMethod.Head, url), cts.Token);
            return (true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, 0, Short(ex));
        }
    }

    /// <summary>TLS 1.3 + GET: читаем до 64 КБ. Если соединение «замерзает» посередине — это блокировка по объёму.</summary>
    private static async Task<(bool ok, bool stream, long ms, string? err)> Stream(HttpMessageInvoker c, string url, int timeoutSec, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec + 1));
        var sw = Stopwatch.StartNew();
        bool gotHeaders = false;
        long headersMs = 0;
        long read = 0;
        try
        {
            var req = Req(HttpMethod.Get, url);
            using var resp = await c.SendAsync(req, cts.Token);
            gotHeaders = true;
            headersMs = sw.ElapsedMilliseconds;
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[16384];
            while (read < StreamBytes)
            {
                int n = await s.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                read += n;
            }
            return (true, true, headersMs, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (gotHeaders)
                return (true, false, headersMs, $"обрыв после {read / 1024} кб (похоже на тспу)");
            return (false, false, 0, Short(ex));
        }
    }

    private static string Short(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex switch
        {
            OperationCanceledException => "таймаут",
            TimeoutException => NoTcp,
            System.Net.Sockets.SocketException se => se.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionReset => "сброс соединения",
                System.Net.Sockets.SocketError.HostNotFound => "домен не найден (dns)",
                System.Net.Sockets.SocketError.TimedOut => "таймаут",
                _ => se.SocketErrorCode.ToString(),
            },
            System.IO.IOException => "обрыв tls",
            AuthenticationException => "ошибка tls",
            _ => ex.Message,
        };
    }
}

/// <summary>Перебор стратегий: запускаем winws, проверяем сервисы, считаем баллы.</summary>
public static class StrategyPicker
{
    public sealed record Progress(Strategy Strategy, TestReport Report, int Index, int Count);

    public static IEnumerable<Strategy> Order(IEnumerable<Strategy> all, AppSettings s) =>
        all.OrderByDescending(x => x.Name == s.LastGoodStrategy)
           .ThenByDescending(x => s.Scores.TryGetValue(x.Name, out var v) ? v : -1)
           .ThenBy(x => x.IsHub); // штатные раньше экспериментальных при равенстве

    /// <param name="stopAtScore">Быстрый режим: остановиться на первой стратегии с баллом не ниже этого (1000 = всё работает). null — проверить все.</param>
    public static async Task<(Strategy? best, TestReport? report)> RunAsync(
        IReadOnlyList<Strategy> candidates, AppSettings settings, int? stopAtScore,
        IProgress<Progress>? progress, CancellationToken ct, int timeoutSec = 4)
    {
        var targets = ServiceCatalog.Targets(settings);
        if (targets.Count == 0) throw new InvalidOperationException("не выбран ни один сервис для проверки.");

        Strategy? best = null;
        TestReport? bestReport = null;
        int i = 0;
        foreach (var s in candidates)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            TestReport rep;
            var err = await ZapretEngine.StartAsync(s, settings);
            if (err != null)
            {
                rep = new TestReport { StartError = err };
                Log.Write($"{s.Name}: не запустилась — {err}");
            }
            else
            {
                await Task.Delay(400, ct); // даём WinDivert подхватить фильтры
                rep = await Tester.RunAsync(targets, timeoutSec, ct);
                var bad = rep.Checks.Where(c => !c.Ok && !c.IpBlocked)
                    .Select(c => $"{new Uri(c.Url).Host}[{(c.Tls12 ? "" : "1.2 ")}{(c.Tls13 ? "" : "1.3 ")}{(c.Stream ? "" : "64K")}]{(c.Error != null ? ": " + c.Error : "")}");
                var ipb = rep.Checks.Where(c => c.IpBlocked).Select(c => new Uri(c.Url).Host).ToList();
                Log.Write($"{s.Name}: {rep.Passed}/{rep.Total}, {rep.AvgMs} мс" + (rep.Perfect ? "" : " · " + string.Join("; ", bad))
                          + (ipb.Count > 0 ? " · блок по ip (не считается): " + string.Join(", ", ipb) : ""));
            }
            settings.Scores[s.Name] = rep.Score;
            progress?.Report(new Progress(s, rep, i, candidates.Count));

            if (best == null || bestReport == null || rep.Passed > bestReport.Passed ||
                (rep.Passed == bestReport.Passed && rep.AvgMs > 0 && (bestReport.AvgMs == 0 || rep.AvgMs < bestReport.AvgMs)))
            {
                best = s; bestReport = rep;
            }
            if (stopAtScore.HasValue && rep.Passed > 0 && rep.Score >= stopAtScore.Value) break;
        }

        if (best != null && bestReport != null && bestReport.Passed > 0)
        {
            if (ZapretEngine.RunningStrategy != best.Name)
            {
                var error = await ZapretEngine.StartAsync(best, settings);
                if (error != null) throw new InvalidOperationException(error);
            }
            settings.LastGoodStrategy = best.Name;
            settings.SelectedStrategy = best.Name;
            settings.LastAutoPick = DateTime.Now;
        }
        else
        {
            await ZapretEngine.StopAsync();
            best = null;
        }
        settings.Save();
        return (best, bestReport);
    }
}
