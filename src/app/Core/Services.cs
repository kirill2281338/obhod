using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Obhod.Core;

public sealed class ServiceDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Hint { get; init; } = "";
    public string Glyph { get; init; } = "●";
    /// <summary>Домены, которых нет в штатных списках запрета: добавляются в list-general-user.txt при включении.</summary>
    public string[] ExtraDomains { get; init; } = Array.Empty<string>();
    public required string[] TestUrls { get; init; }
    public bool Custom { get; init; }
}

public static class ServiceCatalog
{
    public static readonly IReadOnlyList<ServiceDef> BuiltIn = new[]
    {
        new ServiceDef { Id = "discord", Name = "Discord", Glyph = "D", Hint = "чат, голос, картинки",
            TestUrls = new[] { "https://discord.com", "https://gateway.discord.gg", "https://cdn.discordapp.com", "https://media.discordapp.net", "https://updates.discord.com" } },
        new ServiceDef { Id = "youtube", Name = "YouTube", Glyph = "▶", Hint = "сайт, превью, видеопоток",
            TestUrls = new[] { "https://www.youtube.com", "https://i.ytimg.com", "https://redirector.googlevideo.com", "https://yt3.ggpht.com" } },
        new ServiceDef { Id = "cloudflare", Name = "сайты на cloudflare", Glyph = "☁", Hint = "тысячи сайтов за cloudflare",
            TestUrls = new[] { "https://www.cloudflare.com", "https://cdnjs.cloudflare.com" } },
        new ServiceDef { Id = "twitch", Name = "Twitch", Glyph = "T", Hint = "стримы и чат",
            ExtraDomains = new[] { "twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net" },
            TestUrls = new[] { "https://www.twitch.tv", "https://static.twitchcdn.net" } },
        new ServiceDef { Id = "x", Name = "X (Twitter)", Glyph = "X", Hint = "может требовать vpn: блок по ip",
            ExtraDomains = new[] { "x.com", "twitter.com", "twimg.com", "t.co" },
            TestUrls = new[] { "https://x.com", "https://abs.twimg.com" } },
        new ServiceDef { Id = "instagram", Name = "Instagram", Glyph = "◎", Hint = "может требовать vpn: блок по ip",
            ExtraDomains = new[] { "instagram.com", "cdninstagram.com", "ig.me" },
            TestUrls = new[] { "https://www.instagram.com" } },
        new ServiceDef { Id = "facebook", Name = "Facebook", Glyph = "f", Hint = "может требовать vpn: блок по ip",
            ExtraDomains = new[] { "facebook.com", "fbcdn.net", "facebook.net", "fb.com" },
            TestUrls = new[] { "https://www.facebook.com" } },
        new ServiceDef { Id = "whatsapp", Name = "WhatsApp Web", Glyph = "W", Hint = "веб-версия и медиа",
            ExtraDomains = new[] { "whatsapp.com", "whatsapp.net", "wa.me" },
            TestUrls = new[] { "https://web.whatsapp.com" } },
        new ServiceDef { Id = "telegramweb", Name = "Telegram Web", Glyph = "✈", Hint = "web.telegram.org, t.me, telegra.ph",
            ExtraDomains = new[] { "telegram.org", "t.me", "telegra.ph", "telegram.me" },
            TestUrls = new[] { "https://web.telegram.org", "https://telegram.org" } },
        new ServiceDef { Id = "rutracker", Name = "Rutracker", Glyph = "R", Hint = "форум и трекер",
            ExtraDomains = new[] { "rutracker.org", "rutracker.cc", "rutracker.net" },
            TestUrls = new[] { "https://rutracker.org" } },
        new ServiceDef { Id = "soundcloud", Name = "SoundCloud", Glyph = "♫", Hint = "музыка",
            ExtraDomains = new[] { "soundcloud.com", "sndcdn.com" },
            TestUrls = new[] { "https://soundcloud.com" } },
        new ServiceDef { Id = "roblox", Name = "Roblox", Glyph = "■", Hint = "сайт и лаунчер; для игры включите игровой фильтр",
            ExtraDomains = new[] { "roblox.com", "rbxcdn.com", "roblox.cn", "rbx.com" },
            TestUrls = new[] { "https://www.roblox.com" } },
        new ServiceDef { Id = "linkedin", Name = "LinkedIn", Glyph = "in", Hint = "может требовать vpn",
            ExtraDomains = new[] { "linkedin.com", "licdn.com" },
            TestUrls = new[] { "https://www.linkedin.com" } },
        new ServiceDef { Id = "patreon", Name = "Patreon", Glyph = "P", Hint = "подписки авторов",
            ExtraDomains = new[] { "patreon.com", "patreonusercontent.com" },
            TestUrls = new[] { "https://www.patreon.com" } },
    };

    public static IEnumerable<ServiceDef> All(AppSettings s) =>
        BuiltIn.Concat(s.CustomServices.Select(ToDef));

    public static ServiceDef ToDef(CustomService c) => new()
    {
        Id = "custom:" + c.Name,
        Name = c.Name,
        Glyph = c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "?",
        Hint = string.Join(", ", c.Domains),
        ExtraDomains = c.Domains.ToArray(),
        TestUrls = c.Domains.Take(2).Select(d => "https://" + d).ToArray(),
        Custom = true,
    };

    public static List<(string service, string url)> Targets(AppSettings s) =>
        All(s).Where(d => s.EnabledServices.Contains(d.Id))
              .SelectMany(d => d.TestUrls.Select(u => (d.Name, u)))
              .ToList();

    public static string NormalizeDomain(string raw)
    {
        var d = raw.Trim().ToLowerInvariant();
        if (d.Contains("://")) d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = d.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (slash >= 0) d = d[..slash];
        if (d.StartsWith("www.")) d = d[4..];
        return d.Trim('.');
    }

    private const string Begin = "# >>> obhod (не редактируйте этот блок вручную)";
    private const string End = "# <<< obhod";
    private static readonly string LegacyBegin = "# >>> " + "Zapret" + "Hub";
    private static readonly string LegacyEnd = "# <<< " + "Zapret" + "Hub";

    /// <summary>
    /// Пишет домены включённых сервисов в list-general-user.txt (в копии запрета внутри программы),
    /// не трогая строки, которые пользователь добавил сам.
    /// </summary>
    public static void WriteUserList(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Paths.ZapretLists);
            var file = Path.Combine(Paths.ZapretLists, "list-general-user.txt");
            var keep = new List<string>();
            if (File.Exists(file))
            {
                bool inBlock = false;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.StartsWith("# >>> obhod") || line.StartsWith(LegacyBegin)) { inBlock = true; continue; }
                    if (line.StartsWith(End) || line.StartsWith(LegacyEnd)) { inBlock = false; continue; }
                    if (!inBlock) keep.Add(line);
                }
            }
            if (!keep.Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#")))
                keep = new List<string> { "# Never leave this file empty", "domain.example.abc" };

            var domains = All(s).Where(d => s.EnabledServices.Contains(d.Id))
                                .SelectMany(d => d.ExtraDomains)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var output = new List<string>(keep);
            if (domains.Count > 0)
            {
                output.Add(Begin);
                output.AddRange(domains);
                output.Add(End);
            }
            File.WriteAllLines(file, output, new UTF8Encoding(false));

            var ipx = Path.Combine(Paths.ZapretLists, "ipset-exclude-user.txt");
            if (!File.Exists(ipx)) File.WriteAllText(ipx, "203.0.113.113/32\r\n");
            var lx = Path.Combine(Paths.ZapretLists, "list-exclude-user.txt");
            if (!File.Exists(lx)) File.WriteAllText(lx, "domain.example.abc\r\n");
        }
        catch (Exception ex) { Log.Write("не удалось записать список доменов: " + ex.Message); }
    }
}
