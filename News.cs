using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Zimbar;

public record NewsItem(string Title, string Source, string Link, DateTime When, string Image);

/// <summary>
/// Notícias por RSS do Bing News em pt-BR (ecossistema MSN) — vem com a imagem
/// da manchete (News:Image), o que o feed do Google não oferece.
/// Cache de 10 minutos por categoria.
/// </summary>
public static class News
{
    public static readonly (string Label, string Query)[] Categorias =
    {
        ("destaques", "brasil"),
        ("mundo", "mundo"),
        ("tecnologia", "tecnologia"),
        ("negócios", "economia"),
        ("esportes", "esporte futebol"),
        ("entretenimento", "famosos celebridades"),
    };

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, (DateTime At, List<NewsItem> Items)> Cache = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Zimbar/0.6");
        return c;
    }

    public static async Task<List<NewsItem>> Fetch(string query, bool force = false)
    {
        if (!force && Cache.TryGetValue(query, out var hit) && DateTime.Now - hit.At < TimeSpan.FromMinutes(10))
            return hit.Items;

        // Tenta com filtro de 7 dias (fresco); se vier vazio, tenta sem filtro.
        var items = await FetchRaw(query, semana: true);
        if (items.Count == 0) items = await FetchRaw(query, semana: false);

        Cache[query] = (DateTime.Now, items);
        return items;
    }

    private static async Task<List<NewsItem>> FetchRaw(string query, bool semana)
    {
        string url = $"https://www.bing.com/news/search?q={Uri.EscapeDataString(query)}&format=rss&mkt=pt-br"
                   + (semana ? "&qft=interval%3d%227%22" : "");
        string xmlText;
        try { xmlText = await Http.GetStringAsync(url); }
        catch { return new List<NewsItem>(); }

        XDocument doc;
        try { doc = XDocument.Parse(xmlText); }
        catch { return new List<NewsItem>(); }

        // O namespace News: do Bing muda a cada consulta; busca por LocalName.
        static string El(XElement item, string localName)
            => item.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value ?? "";

        return doc.Descendants("item").Select(i =>
        {
            string img = El(i, "Image");
            if (img.StartsWith("http://")) img = "https://" + img[7..]; // https carrega no WPF
            else if (img.StartsWith('/')) img = "https://www.bing.com" + img;
            if (img.Length > 0) img += "&w=400&h=220&c=14"; // recorte no tamanho do card

            DateTime when = default;
            if (DateTime.TryParse(El(i, "pubDate"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                when = u.ToLocalTime();

            return new NewsItem(El(i, "title"), El(i, "Source"), El(i, "link"), when, img);
        })
        .Where(n => n.Title.Length > 0)
        .OrderByDescending(n => n.When)
        .Take(21)
        .ToList();
    }
}
