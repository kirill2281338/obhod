using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Win32;

namespace Obhod.Core;

/// <summary>Сервер VPN: исходная ссылка (как в Happ) или готовый outbound Xray из подписки.</summary>
public sealed class VpnServer
{
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? Link { get; set; }
    public string? OutboundJson { get; set; }
    public string? Subscription { get; set; }
    public const string AutoId = "auto";
    private string? _fixedId;
    public string Id
    {
        get => _fixedId ?? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Link ?? OutboundJson ?? Name)), 0, 8).ToLowerInvariant();
        set => _fixedId = value;
    }

    /// <summary>
    /// Служебные строки подписок (Remnawave для Happ): «авто», разделители «… ниже». Это не настоящие серверы.
    /// </summary>
    public bool IsPlaceholder
    {
        get
        {
            var n = Name.ToLowerInvariant();
            return Host is "web.max.ru" or "0.0.0.0" or "127.0.0.1" or "localhost"
                   || n.Contains("авто") || n.Contains("auto") || n.Contains("быстрейш")
                   || n.Contains("ниже") || n.Contains("выше");
        }
    }
}

public sealed class VpnSettings
{
    public List<string> Subscriptions { get; set; } = new();
    public List<VpnServer> Servers { get; set; } = new();
    public string? SelectedId { get; set; }
    /// <summary>proxy — системный прокси (браузеры и большинство программ), tun — весь трафик компьютера.</summary>
    public string Mode { get; set; } = "proxy";
    public bool RuDirect { get; set; } = true;
}

/// <summary>Разбор ссылок и подписок в форматах, которые понимает Happ.</summary>
public static class VpnLinks
{
    public static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(30) };
        // Панели (Marzban, Remnawave, 3x-ui) отдают этому клиенту список ссылок в base64
        c.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.13");
        return c;
    }

    /// <summary>Добавляет то, что вставил пользователь: ссылку, несколько ссылок, подписку или happ://add/...</summary>
    public static async Task<(List<VpnServer> servers, string? subscription, string? error)> ParseInputAsync(string input, CancellationToken ct)
    {
        input = input.Trim();
        if (input.StartsWith("happ://crypt", StringComparison.OrdinalIgnoreCase))
            return (new(), null, "это зашифрованная ссылка happ (happ://crypt…). её может расшифровать только сам happ. попросите у продавца обычную ссылку на подписку (https://…) или ссылки vless://.");
        // Ссылки клиента Incy: incy://add/<url>, incy://crypt1/<base64url> (внутри обычная ссылка), incy://import/<base64-конфиг>
        if (input.StartsWith("incy://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input["incy://".Length..];
            if (rest.StartsWith("add/", StringComparison.OrdinalIgnoreCase)) input = rest[4..];
            else if (rest.StartsWith("crypt1/", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("import/", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = TryBase64(rest[(rest.IndexOf('/') + 1)..]);
                if (decoded == null) return (new(), null, "не удалось раскрыть ссылку incy.");
                decoded = decoded.Trim();
                if (decoded.StartsWith("incy://", StringComparison.OrdinalIgnoreCase)) return await ParseInputAsync(decoded, ct);
                if (decoded.StartsWith("{") || decoded.StartsWith("["))
                {
                    var fromJson = ParseXrayJson(decoded);
                    return fromJson.Count == 0 ? (fromJson, null, "в конфиге incy не нашлось поддерживаемых серверов.") : (fromJson, null, null);
                }
                input = decoded;
            }
            else return (new(), null, "эта ссылка incy управляет самим приложением incy (подключение или маршруты), серверов в ней нет.");
        }
        if (input.StartsWith("happ://add/", StringComparison.OrdinalIgnoreCase))
            input = input["happ://add/".Length..];
        else if (input.StartsWith("happ://", StringComparison.OrdinalIgnoreCase))
            input = input["happ://".Length..];

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var list = await FetchSubscriptionAsync(input, ct);
            // Панель вместо серверов прислала «заглушку» с сообщением (лимит устройств, неподдерживаемое приложение и т. п.)
            if (list.Count == 1 && IsPanelNotice(list[0].Name))
                return (new(), null, "сервис vpn ответил: «" + list[0].Name + "». " +
                    (list[0].Name.Contains("устройств", StringComparison.OrdinalIgnoreCase)
                        ? "удалите одно из старых устройств в боте или личном кабинете сервиса и добавьте подписку ещё раз."
                        : "обратитесь в поддержку сервиса."));
            return list.Count == 0
                ? (list, null, "в подписке не нашлось серверов, которые понимает программа.")
                : (list, input, null);
        }
        var servers = ParseMany(input);
        return servers.Count == 0 ? (servers, null, "не распознал ссылку. поддерживаются vless://, vmess://, trojan://, ss:// и подписки https://.") : (servers, null, null);
    }

    public static async Task<List<VpnServer>> FetchSubscriptionAsync(string url, CancellationToken ct)
    {
        // Панели Remnawave с лимитом устройств отвечают «App not supported», если клиент не прислал HWID.
        // Шлём постоянный идентификатор этого компьютера, как это делают Happ и Incy (займёт одно место в лимите устройств).
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-hwid", DeviceId());
        req.Headers.TryAddWithoutValidation("x-device-os", "Windows");
        req.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
        req.Headers.TryAddWithoutValidation("x-device-model", "obhod");
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var text = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        List<VpnServer> list;
        if (text.StartsWith("[") || text.StartsWith("{")) list = ParseXrayJson(text);
        else
        {
            var decoded = TryBase64(text);
            list = ParseMany(decoded ?? text);
        }
        foreach (var s in list) s.Subscription = url;
        return list;
    }

    private static bool IsPanelNotice(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("not supported") || n.Contains("не поддерж") || n.Contains("устройств") || n.Contains("device")
            || n.Contains("превышен") || n.Contains("limit") || n.Contains("истек") || n.Contains("expired") || n.Contains("продлите");
    }

    /// <summary>Постоянный ID устройства: хеш MachineGuid Windows (сам GUID наружу не уходит).</summary>
    public static string DeviceId()
    {
        string seed;
        try { seed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") as string ?? Environment.MachineName; }
        catch { seed = Environment.MachineName; }
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("Zapret" + "Hub" + ":" + seed));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static List<VpnServer> ParseMany(string text)
    {
        var res = new List<VpnServer>();
        foreach (var raw in text.Replace("\r", "\n").Split('\n', ' ', '\t'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                var s = ParseLink(line);
                if (s != null) res.Add(s);
            }
            catch (Exception ex) { Log.Write("vpn: не разобрал ссылку: " + ex.Message); }
        }
        return res;
    }

    public static VpnServer? ParseLink(string link)
    {
        var scheme = link.Split("://")[0].ToLowerInvariant();
        switch (scheme)
        {
            case "vless":
            case "trojan":
            {
                var u = new Uri(link);
                return new VpnServer
                {
                    Protocol = scheme, Host = u.IdnHost, Port = u.Port, Link = link,
                    Name = Uri.UnescapeDataString(u.Fragment.TrimStart('#')) is { Length: > 0 } n ? n : $"{u.IdnHost}:{u.Port}",
                };
            }
            case "vmess":
            {
                var json = TryBase64(link[8..]) ?? throw new FormatException("vmess без base64");
                var o = JsonNode.Parse(json)!;
                return new VpnServer
                {
                    Protocol = "vmess", Host = Str(o["add"]), Port = Int(o["port"]), Link = link,
                    Name = Str(o["ps"]) is { Length: > 0 } n ? n : Str(o["add"]),
                };
            }
            case "ss":
            {
                var (method, pass, host, port, name) = ParseSs(link);
                return new VpnServer { Protocol = "shadowsocks", Host = host, Port = port, Link = link, Name = name.Length > 0 ? name : $"{host}:{port}" };
            }
            case "hysteria2":
            case "hy2":
            case "tuic":
            case "wireguard":
                throw new NotSupportedException($"протокол {scheme} пока не поддерживается");
            default:
                return null;
        }
    }

    /// <summary>Подписки, которые отдают готовые конфиги Xray (Remnawave для Happ/v2rayN).</summary>
    private static List<VpnServer> ParseXrayJson(string text)
    {
        var res = new List<VpnServer>();
        var root = JsonNode.Parse(text);
        var configs = root is JsonArray arr ? arr.ToList() : new List<JsonNode?> { root };
        foreach (var cfg in configs)
        {
            if (cfg?["outbounds"] is not JsonArray outs) continue;
            var remark = Str(cfg["remarks"]);
            foreach (var ob in outs)
            {
                var proto = Str(ob?["protocol"]);
                if (proto is not ("vless" or "vmess" or "trojan" or "shadowsocks")) continue;
                var copy = JsonNode.Parse(ob!.ToJsonString())!;
                copy["tag"] = "proxy";
                string host = ""; int port = 0;
                var st = copy["settings"];
                var first = st?["vnext"]?[0] ?? st?["servers"]?[0] ?? st;
                host = Str(first?["address"]); port = Int(first?["port"]);
                res.Add(new VpnServer
                {
                    Protocol = proto, Host = host, Port = port, OutboundJson = copy.ToJsonString(),
                    Name = remark.Length > 0 ? remark : $"{host}:{port}",
                });
                break; // в одном конфиге — один сервер
            }
        }
        return res;
    }

    // ---------- Xray outbound из ссылки ----------

    public static JsonObject BuildOutbound(VpnServer s)
    {
        if (s.OutboundJson != null) return (JsonObject)JsonNode.Parse(s.OutboundJson)!;
        var link = s.Link!;
        var scheme = link.Split("://")[0].ToLowerInvariant();
        if (scheme == "vmess") return VmessOutbound(link);
        if (scheme == "ss")
        {
            var (method, pass, host, port, _) = ParseSs(link);
            return new JsonObject
            {
                ["tag"] = "proxy", ["protocol"] = "shadowsocks",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = host, ["port"] = port, ["method"] = method, ["password"] = pass }) },
            };
        }

        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        var user = Uri.UnescapeDataString(u.UserInfo);
        JsonObject settings = scheme == "vless"
            ? new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = u.IdnHost, ["port"] = u.Port,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = user, ["encryption"] = q["encryption"] ?? "none", ["flow"] = q["flow"] ?? "" }),
                }),
            }
            : new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = u.IdnHost, ["port"] = u.Port, ["password"] = user }) };

        return new JsonObject
        {
            ["tag"] = "proxy", ["protocol"] = scheme, ["settings"] = settings,
            ["streamSettings"] = Stream(q["type"] ?? "tcp", q["security"] ?? (scheme == "trojan" ? "tls" : "none"),
                q["sni"] ?? q["peer"], q["host"], q["path"], q["serviceName"], q["mode"], q["headerType"],
                q["fp"], q["alpn"], q["pbk"], q["sid"], q["spx"], q["allowInsecure"] == "1" || q["insecure"] == "1", q["extra"], q["pqv"]),
        };
    }

    private static JsonObject VmessOutbound(string link)
    {
        var o = JsonNode.Parse(TryBase64(link[8..])!)!;
        return new JsonObject
        {
            ["tag"] = "proxy", ["protocol"] = "vmess",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = Str(o["add"]), ["port"] = Int(o["port"]),
                    ["users"] = new JsonArray(new JsonObject { ["id"] = Str(o["id"]), ["alterId"] = Int(o["aid"]), ["security"] = Str(o["scy"]) is { Length: > 0 } sc ? sc : "auto" }),
                }),
            },
            ["streamSettings"] = Stream(Str(o["net"]) is { Length: > 0 } n ? n : "tcp", Str(o["tls"]) is { Length: > 0 } t ? t : "none",
                Str(o["sni"]), Str(o["host"]), Str(o["path"]), Str(o["path"]), Str(o["mode"]), Str(o["type"]), Str(o["fp"]), Str(o["alpn"]),
                null, null, null, false, null, null),
        };
    }

    private static JsonObject Stream(string net, string security, string? sni, string? host, string? path, string? serviceName,
        string? mode, string? headerType, string? fp, string? alpn, string? pbk, string? sid, string? spx, bool insecure, string? extra, string? pqv)
    {
        if (net == "h2") net = "http";
        var st = new JsonObject { ["network"] = net, ["security"] = security };
        if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = sni ?? host ?? "", ["allowInsecure"] = insecure, ["fingerprint"] = string.IsNullOrEmpty(fp) ? "chrome" : fp };
            if (!string.IsNullOrEmpty(alpn)) tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());
            st["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            var r = new JsonObject
            {
                ["serverName"] = sni ?? "", ["fingerprint"] = string.IsNullOrEmpty(fp) ? "chrome" : fp,
                ["publicKey"] = pbk ?? "", ["shortId"] = sid ?? "", ["spiderX"] = spx ?? "",
            };
            if (!string.IsNullOrEmpty(pqv)) r["mldsa65Verify"] = pqv;
            st["realitySettings"] = r;
        }
        switch (net)
        {
            case "ws":
                st["wsSettings"] = new JsonObject { ["path"] = path ?? "/", ["host"] = host ?? "" };
                break;
            case "grpc":
                st["grpcSettings"] = new JsonObject { ["serviceName"] = serviceName ?? path ?? "", ["multiMode"] = mode == "multi" };
                break;
            case "httpupgrade":
                st["httpupgradeSettings"] = new JsonObject { ["path"] = path ?? "/", ["host"] = host ?? "" };
                break;
            case "xhttp":
            case "splithttp":
                st["network"] = "xhttp";
                var x = new JsonObject { ["path"] = path ?? "/", ["host"] = host ?? "", ["mode"] = string.IsNullOrEmpty(mode) ? "auto" : mode };
                if (!string.IsNullOrEmpty(extra)) { try { x["extra"] = JsonNode.Parse(extra); } catch { } }
                st["xhttpSettings"] = x;
                break;
            case "http":
                st["httpSettings"] = new JsonObject { ["path"] = path ?? "/", ["host"] = new JsonArray((host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => (JsonNode)h).ToArray()) };
                break;
            case "tcp" when headerType == "http":
                st["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["request"] = new JsonObject
                        {
                            ["path"] = new JsonArray((JsonNode)(path ?? "/")),
                            ["headers"] = new JsonObject { ["Host"] = new JsonArray((host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => (JsonNode)h).ToArray()) },
                        },
                    },
                };
                break;
        }
        return st;
    }

    private static (string method, string pass, string host, int port, string name) ParseSs(string link)
    {
        var body = link[5..];
        var name = "";
        var hash = body.IndexOf('#');
        if (hash >= 0) { name = Uri.UnescapeDataString(body[(hash + 1)..]); body = body[..hash]; }
        var qm = body.IndexOf('?');
        if (qm >= 0) body = body[..qm];
        string userInfo, hostPort;
        var at = body.LastIndexOf('@');
        if (at < 0) // старый формат: всё в base64
        {
            var dec = TryBase64(body) ?? throw new FormatException("ss без base64");
            at = dec.LastIndexOf('@');
            userInfo = dec[..at]; hostPort = dec[(at + 1)..];
        }
        else
        {
            userInfo = Uri.UnescapeDataString(body[..at]);
            hostPort = body[(at + 1)..].TrimEnd('/');
            if (!userInfo.Contains(':')) userInfo = TryBase64(userInfo) ?? userInfo;
        }
        var colon = userInfo.IndexOf(':');
        var hp = hostPort.LastIndexOf(':');
        return (userInfo[..colon], userInfo[(colon + 1)..], hostPort[..hp].Trim('[', ']'), int.Parse(hostPort[(hp + 1)..]), name);
    }

    private static string? TryBase64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\n", "").Replace("\r", "");
        var hash = s.IndexOf('#'); if (hash >= 0) s = s[..hash];
        s = s.TrimEnd('=');
        s += new string('=', (4 - s.Length % 4) % 4);
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return null; }
    }

    private static string Str(JsonNode? n) => n == null ? "" : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString().Trim('"');
    private static int Int(JsonNode? n) => int.TryParse(Str(n), out var v) ? v : 0;
}

/// <summary>Запуск ядра Xray (и sing-box для режима «весь трафик»), системный прокси, пинг.</summary>
public static class VpnEngine
{
    // Порты подбираются при каждом подключении: стандартные 10808/10809 часто заняты другими клиентами (Happ, Incy, v2rayN)
    public const int PortBase = 10870, PortLast = 10898;
    public static int SocksPort { get; private set; } = PortBase;
    public static int HttpPort { get; private set; } = PortBase + 1;

    private static void PickPorts()
    {
        for (int b = PortBase; b < PortLast; b += 2)
            if (PortFree(b) && PortFree(b + 1)) { SocksPort = b; HttpPort = b + 1; return; }
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); SocksPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var l2 = new TcpListener(System.Net.IPAddress.Loopback, 0); l2.Start(); HttpPort = ((System.Net.IPEndPoint)l2.LocalEndpoint).Port; l2.Stop();
    }

    private static bool PortFree(int port)
    {
        try { var l = new TcpListener(System.Net.IPAddress.Loopback, port); l.Start(); l.Stop(); return true; }
        catch { return false; }
    }

    public static string Dir => Path.Combine(Paths.Root, "vpn");
    public static string XrayDir => Path.Combine(Dir, "xray");
    public static string XrayExe => Path.Combine(XrayDir, "xray.exe");
    public static string SingDir => Path.Combine(Dir, "sing-box");
    public static string SingExe => Path.Combine(SingDir, "sing-box.exe");

    public static bool XrayInstalled => File.Exists(XrayExe);
    public static bool SingInstalled => File.Exists(SingExe);

    private static Process? _xray, _sing;
    public static bool IsRunning => _xray is { HasExited: false };
    public static string? LastError { get; private set; }

    public static async Task<string?> ConnectAsync(VpnServer server, VpnSettings vs)
    {
        await DisconnectAsync();
        if (!XrayInstalled) return "сначала скачайте ядро xray.";
        if (vs.Mode == "tun" && !SingInstalled) return "для режима «весь трафик» скачайте ядро sing-box.";

        PickPorts();
        var cfgPath = Path.Combine(Dir, "xray-config.json");
        File.WriteAllText(cfgPath, BuildXrayConfig(server, vs).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var err = await StartAsync(XrayExe, $"run -c \"{cfgPath}\"", XrayDir, p => _xray = p);
        if (err != null)
            return err.Contains("bind", StringComparison.OrdinalIgnoreCase)
                ? "порт занят другим vpn-клиентом (happ, incy, v2rayn). закройте его или попробуйте ещё раз."
                : "xray не запустился: " + (err.Split('|').LastOrDefault()?.Trim() ?? err);

        if (vs.Mode == "tun")
        {
            var sbPath = Path.Combine(Dir, "sing-box-tun.json");
            File.WriteAllText(sbPath, BuildTunConfig(vs).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            err = await StartAsync(SingExe, $"run -c \"{sbPath}\"", SingDir, p => _sing = p, 2500);
            if (err != null) { await DisconnectAsync(); return "sing-box (tun) не запустился: " + err; }
        }
        else SystemProxy.Enable($"127.0.0.1:{HttpPort}");
        Log.Write($"vpn подключён: {server.Name} ({vs.Mode})" + (server.Id == VpnServer.AutoId ? $", серверов в авто: {RealServers(vs).Count}" : ""));
        return null;
    }

    public static async Task DisconnectAsync()
    {
        SystemProxy.Restore();
        foreach (var p in new[] { _sing, _xray })
        {
            if (p == null) continue;
            try { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(new CancellationTokenSource(4000).Token); } } catch { }
            p.Dispose();
        }
        _sing = _xray = null;
        // на случай, если остались процессы от прошлого запуска программы
        foreach (var name in new[] { "xray", "sing-box" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (p.MainModule?.FileName?.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) == true) p.Kill(true); } catch { }
                p.Dispose();
            }
    }

    private static async Task<string?> StartAsync(string exe, string args, string dir, Action<Process> keep, int waitMs = 1200)
    {
        var tail = new LinkedList<string>();
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["XRAY_LOCATION_ASSET"] = XrayDir;
        var p = Process.Start(psi)!;
        void Collect(string? l) { if (string.IsNullOrWhiteSpace(l)) return; lock (tail) { tail.AddLast(l); if (tail.Count > 8) tail.RemoveFirst(); } }
        p.OutputDataReceived += (_, e) => Collect(e.Data);
        p.ErrorDataReceived += (_, e) => Collect(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(waitMs));
        if (p.HasExited)
        {
            await Task.Delay(50);
            string t; lock (tail) t = string.Join(" | ", tail.TakeLast(3));
            LastError = t;
            p.Dispose();
            return t.Length > 0 ? t : "процесс сразу завершился";
        }
        keep(p);
        return null;
    }

    /// <summary>Настоящие серверы подписки (без служебных строк).</summary>
    public static List<VpnServer> RealServers(VpnSettings vs) => vs.Servers.Where(x => !x.IsPlaceholder).ToList();

    public static JsonObject BuildXrayConfig(VpnServer server, VpnSettings vs)
    {
        bool auto = server.Id == VpnServer.AutoId;
        var rules = new JsonArray
        {
            new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" },
        };
        if (vs.RuDirect)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("geosite:category-ru", "regexp:.*\\.ru$", "regexp:.*\\.xn--p1ai$"), ["outboundTag"] = "direct" });
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:ru"), ["outboundTag"] = "direct" });
        }

        var outbounds = new JsonArray();
        JsonObject? observatory = null;
        JsonArray? balancers = null;
        if (auto)
        {
            // все серверы подписки: ядро само проверяет их каждые 30 секунд и ведёт трафик через самый быстрый живой
            int i = 0, skipped = 0;
            foreach (var srv in RealServers(vs).Take(40))
            {
                try
                {
                    var ob = VpnLinks.BuildOutbound(srv);
                    ob["tag"] = "p" + i++;
                    outbounds.Add(ob);
                }
                catch (Exception ex) { Log.Write($"vpn: пропускаю {srv.Name}: {ex.Message}"); }
            }
            observatory = new JsonObject
            {
                ["subjectSelector"] = new JsonArray("p"),
                ["probeUrl"] = "https://www.google.com/generate_204",
                ["probeInterval"] = "30s",
                ["enableConcurrency"] = true,
            };
            balancers = new JsonArray(new JsonObject
            {
                ["tag"] = "auto", ["selector"] = new JsonArray("p"),
                ["strategy"] = new JsonObject { ["type"] = "leastPing" }, ["fallbackTag"] = "p0",
            });
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["balancerTag"] = "auto" });
        }
        else outbounds.Add(VpnLinks.BuildOutbound(server));
        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });

        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            // адреса и так приходят доменами (http/socks5h, а в режиме tun — от sing-box), поэтому без лишних DNS-запросов
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks", ["listen"] = "127.0.0.1", ["port"] = SocksPort, ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true },
                },
                new JsonObject { ["tag"] = "http", ["listen"] = "127.0.0.1", ["port"] = HttpPort, ["protocol"] = "http" },
            },
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules },
        };
        if (balancers != null) ((JsonObject)cfg["routing"]!)["balancers"] = balancers;
        if (observatory != null) cfg["observatory"] = observatory;
        return cfg;
    }

    /// <summary>Транспорт websocket убран из новых версий xray: такой конфиг не запускается.</summary>
    public static bool UsesRemovedTransport(JsonObject outbound) =>
        (outbound["streamSettings"]?["network"]?.GetValue<string>() ?? "") == "ws";

    /// <summary>Проверка после подключения: открывается ли сайт через VPN. Возвращает время в мс или -1.</summary>
    public static async Task<int> VerifyAsync(int timeoutSec = 10)
    {
        try
        {
            using var h = new HttpClientHandler { Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{SocksPort}"), UseProxy = true };
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            var sw = Stopwatch.StartNew();
            using var r = await c.GetAsync("https://www.google.com/generate_204");
            return r.IsSuccessStatusCode ? (int)sw.ElapsedMilliseconds : -1;
        }
        catch { return -1; }
    }

    /// <summary>Режим «весь трафик»: sing-box создаёт виртуальный адаптер и отправляет всё в Xray через SOCKS.</summary>
    public static JsonObject BuildTunConfig(VpnSettings vs) => new()
    {
        ["log"] = new JsonObject { ["level"] = "warn" },
        ["dns"] = new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject { ["type"] = "tcp", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = "xray" },
                new JsonObject { ["type"] = "local", ["tag"] = "local" },
            },
            ["final"] = "remote",
        },
        ["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "obhod vpn",
                ["address"] = new JsonArray("172.19.0.1/30"), ["mtu"] = 9000,
                ["auto_route"] = true, ["strict_route"] = true, ["stack"] = "mixed",
            },
        },
        ["outbounds"] = new JsonArray
        {
            new JsonObject { ["type"] = "socks", ["tag"] = "xray", ["server"] = "127.0.0.1", ["server_port"] = SocksPort, ["version"] = "5" },
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
        },
        ["route"] = new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "local",
            ["rules"] = new JsonArray
            {
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                // сам Xray и запрет ходят напрямую, иначе получится петля
                new JsonObject { ["process_name"] = new JsonArray("xray.exe", "winws.exe", "TgWsProxy_windows.exe"), ["outbound"] = "direct" },
                new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
            },
            ["final"] = "xray",
        },
    };

    /// <summary>Пинг: время TCP-подключения к серверу.</summary>
    public static async Task<int> PingAsync(VpnServer s, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.Host) || s.Port <= 0) return -1;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(s.Host, s.Port, cts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    // ---------- Ядра: скачиваются только по кнопке, с GitHub ----------

    public const string XrayRepo = "XTLS/Xray-core";
    public const string SingRepo = "SagerNet/sing-box";
    private static bool XrayAsset(string n) => n.Equals("Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase);
    private static bool SingAsset(string n) => n.EndsWith("-windows-amd64.zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Версия ядра на компьютере (записывается при скачивании в version.txt).</summary>
    public static string? LocalVersion(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "version.txt");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : (Directory.Exists(dir) ? "?" : null);
        }
        catch { return null; }
    }

    public static Task<ReleaseInfo?> LatestXrayAsync(CancellationToken ct = default) => LatestAsync(XrayRepo, XrayAsset, ct);
    public static Task<ReleaseInfo?> LatestSingBoxAsync(CancellationToken ct = default) => LatestAsync(SingRepo, SingAsset, ct);

    private static async Task<ReleaseInfo?> LatestAsync(string repo, Func<string, bool> filter, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("obhod/1.0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await VpnLinks.Http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (filter(a.GetProperty("name").GetString() ?? "")) { url = a.GetProperty("browser_download_url").GetString(); break; }
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            return new ReleaseInfo(tag, url, page);
        }
        catch (Exception ex)
        {
            Log.Write($"не удалось узнать версию {repo}: {ex.Message}");
            return null;
        }
    }

    public static async Task InstallXrayAsync(IProgress<double>? progress, CancellationToken ct, ReleaseInfo? rel = null)
    {
        rel ??= await LatestXrayAsync(ct) ?? throw new InvalidOperationException("github не ответил.");
        if (rel.AssetUrl == null) throw new InvalidOperationException("в релизе xray нет файла для windows.");
        await DownloadZip(rel.AssetUrl, XrayDir, progress, ct);
        File.WriteAllText(Path.Combine(XrayDir, "version.txt"), rel.Version);
    }

    public static async Task InstallSingBoxAsync(IProgress<double>? progress, CancellationToken ct, ReleaseInfo? rel = null)
    {
        rel ??= await LatestSingBoxAsync(ct) ?? throw new InvalidOperationException("github не ответил.");
        if (rel.AssetUrl == null) throw new InvalidOperationException("в релизе sing-box нет файла для windows.");
        await DownloadZip(rel.AssetUrl, SingDir, progress, ct);
        // в архиве sing-box всё лежит во вложенной папке — поднимаем файлы наверх
        var exe = Directory.GetFiles(SingDir, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe != null && Path.GetDirectoryName(exe) != SingDir)
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(exe)!))
                File.Move(f, Path.Combine(SingDir, Path.GetFileName(f)), true);
        File.WriteAllText(Path.Combine(SingDir, "version.txt"), rel.Version);
    }

    private static async Task DownloadZip(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.TempDir);
        var zip = Path.Combine(Paths.TempDir, Path.GetFileName(new Uri(url).LocalPath));
        using (var resp = await VpnLinks.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[81920]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        await DisconnectAsync();
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        ZipFile.ExtractToDirectory(zip, dest);
        File.Delete(zip);
        Log.Write("vpn: ядро скачано в " + dest);
    }
}

/// <summary>Системный прокси Windows (как «Прокси-сервер» в настройках). Прежние значения восстанавливаются.</summary>
public static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static (int enable, string? server, string? bypass)? _saved;

    public static void Enable(string server)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true)!;
        _saved ??= ((int)(k.GetValue("ProxyEnable") ?? 0), k.GetValue("ProxyServer") as string, k.GetValue("ProxyOverride") as string);
        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        k.SetValue("ProxyServer", server);
        k.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;172.16.*;192.168.*");
        Refresh();
    }

    public static void Restore()
    {
        if (_saved is not { } s) return;
        using var k = Registry.CurrentUser.OpenSubKey(Key, true)!;
        k.SetValue("ProxyEnable", s.enable, RegistryValueKind.DWord);
        if (s.server != null) k.SetValue("ProxyServer", s.server); else k.DeleteValue("ProxyServer", false);
        if (s.bypass != null) k.SetValue("ProxyOverride", s.bypass); else k.DeleteValue("ProxyOverride", false);
        _saved = null;
        Refresh();
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int len);

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
    }
}
