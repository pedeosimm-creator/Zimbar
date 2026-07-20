using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Zimbar;

/// <summary>
/// Cliente do Acervo (acervozim.netlify.app) — projeto Supabase PRÓPRIO
/// (uooopygrpubjewdewljh). O Apanhador do Zimbar manda capturas pra cá: sobe a
/// imagem no storage (bucket "acervo", pasta "nucleo") e insere em ac_inbox,
/// exatamente como o Apanhador web do Acervo faz. Chave anon já pública no site.
/// </summary>
public static class Acervo
{
    private const string Url = "https://uooopygrpubjewdewljh.supabase.co";
    private const string Key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InVvb29weWdycHViamV3ZGV3bGpoIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODA5MzM0NDgsImV4cCI6MjA5NjUwOTQ0OH0.HLcaN6-SZTLGKgZoKZvVt0zXoMoK5QGrNd9NTpV6slI";

    private static readonly HttpClient Http = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.Add("apikey", Key);
        c.DefaultRequestHeaders.Add("Authorization", "Bearer " + Key);
        return c;
    }

    /// <summary>Sobe uma imagem PNG no storage e devolve o path (nucleo/....png).</summary>
    public static async Task<string> UploadPng(byte[] png)
    {
        string path = $"nucleo/{DateTimeOffset.Now.ToUnixTimeMilliseconds()}-{Random.Shared.Next(0x1000, 0xFFFF):x}.png";
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var req = new HttpRequestMessage(HttpMethod.Post, "/storage/v1/object/acervo/" + path) { Content = content };
        var r = await Http.SendAsync(req);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException("upload falhou: " + r.StatusCode + " " + await r.Content.ReadAsStringAsync());
        return path;
    }

    /// <summary>Insere uma captura no Núcleo (ac_inbox).</summary>
    public static async Task Capturar(string kind, string? texto, string? imagePath, string? transcricao)
    {
        var row = new JsonObject
        {
            ["kind"] = kind,                       // 'foto' | 'print' | 'texto'
            ["text_content"] = string.IsNullOrWhiteSpace(texto) ? null : texto,
            ["image_path"] = imagePath,
            ["transcription"] = string.IsNullOrWhiteSpace(transcricao) ? null : transcricao
        };
        var content = new StringContent(row.ToJsonString(), Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/rest/v1/ac_inbox", content);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException("ac_inbox falhou: " + r.StatusCode + " " + await r.Content.ReadAsStringAsync());
    }

    /// <summary>URL pública de uma imagem do bucket (pra mostrar a prévia).</summary>
    public static string PublicUrl(string path) => $"{Url}/storage/v1/object/public/acervo/{path}";

    // ---- Núcleo: itens capturados esperando encaminhamento --------------

    public sealed class Item
    {
        public string Id = "";
        public string Kind = "texto";
        public string? Text;
        public string? ImagePath;
        public string? Transcription;   // mutável: pode ser preenchida via OCR aqui
        public string Status = "pendente";
        public DateTime CriadoEm;

        public bool HasImage => !string.IsNullOrEmpty(ImagePath);
        public bool HasText => !string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(Transcription);
        public string TextoAtual => (Text ?? Transcription ?? "").Trim();
    }

    private static async Task<JsonArray> Select(string q)
    {
        var r = await Http.GetAsync("/rest/v1/" + q);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonArray ?? new JsonArray();
    }

    /// <summary>Últimos apanhados que ainda esperam ser encaminhados.</summary>
    public static async Task<List<Item>> ListarNucleo(int limit = 20)
    {
        var arr = await Select($"ac_inbox?status=eq.pendente&order=created_at.desc&limit={limit}");
        var list = new List<Item>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            DateTime.TryParse(o["created_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt);
            list.Add(new Item
            {
                Id = o["id"]?.GetValue<string>() ?? "",
                Kind = o["kind"]?.GetValue<string>() ?? "texto",
                Text = o["text_content"]?.GetValue<string>(),
                ImagePath = o["image_path"]?.GetValue<string>(),
                Transcription = o["transcription"]?.GetValue<string>(),
                Status = o["status"]?.GetValue<string>() ?? "pendente",
                CriadoEm = dt.ToLocalTime()
            });
        }
        return list;
    }

    /// <summary>Baixa os bytes de uma imagem do bucket (pra transcrever localmente).</summary>
    public static async Task<byte[]> BaixarImagem(string path)
    {
        var r = await Http.GetAsync("/storage/v1/object/public/acervo/" + path);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Grava a transcrição de um item (OCR) no ac_inbox.</summary>
    public static async Task SalvarTranscricao(string id, string texto)
    {
        await Patch("ac_inbox?id=eq." + Uri.EscapeDataString(id), new JsonObject { ["transcription"] = texto });
    }

    /// <summary>Apaga um apanhado (e a imagem, se tiver).</summary>
    public static async Task Apagar(Item item)
    {
        var r = await Http.DeleteAsync("/rest/v1/ac_inbox?id=eq." + Uri.EscapeDataString(item.Id));
        r.EnsureSuccessStatusCode();
        if (item.HasImage)
            try { await Http.DeleteAsync("/storage/v1/object/acervo/" + item.ImagePath); } catch { }
    }

    // ---- Destinos de encaminhamento -------------------------------------

    public record Destino(string Id, string Nome);
    public record Secao(string Id, string Titulo, string Conteudo);

    public static async Task<List<Destino>> Pastas()
    {
        var arr = await Select("ac_folders?select=id,name&order=position");
        return arr.OfType<JsonObject>().Select(o => new Destino(o["id"]!.GetValue<string>(), o["name"]?.GetValue<string>() ?? "pasta")).ToList();
    }

    public static async Task<List<Destino>> Estudos()
    {
        var arr = await Select("ac_studies?select=id,title&order=position");
        return arr.OfType<JsonObject>().Select(o => new Destino(o["id"]!.GetValue<string>(), o["title"]?.GetValue<string>() ?? "estudo")).ToList();
    }

    public static async Task<List<Secao>> Secoes(string studyId)
    {
        var arr = await Select($"ac_study_sections?study_id=eq.{Uri.EscapeDataString(studyId)}&select=id,title,content&order=position");
        return arr.OfType<JsonObject>().Select(o => new Secao(o["id"]!.GetValue<string>(), o["title"]?.GetValue<string>() ?? "capítulo", o["content"]?.GetValue<string>() ?? "")).ToList();
    }

    public static async Task<List<Destino>> Livros()
    {
        var arr = await Select("ac_books?select=id,title&order=created_at.desc");
        return arr.OfType<JsonObject>().Select(o => new Destino(o["id"]!.GetValue<string>(), o["title"]?.GetValue<string>() ?? "livro")).ToList();
    }

    // ---- Encaminhar (espelha o ForwardModal do Acervo) ------------------

    /// <summary>Encaminha pra uma pasta de Referências.</summary>
    public static async Task EncaminharParaPasta(Item item, Destino pasta)
    {
        string text = item.TextoAtual;
        int count = await Contar("ac_refs?folder_id=eq." + Uri.EscapeDataString(pasta.Id));
        await Post("ac_refs", new JsonObject
        {
            ["folder_id"] = pasta.Id,
            ["kind"] = item.HasImage ? "image" : "text",
            ["title"] = null,
            ["content"] = item.HasImage ? (text.Length > 0 ? text : null) : text,
            ["image_path"] = item.ImagePath,
            ["position"] = count + 1
        });
        await MarcarEncaminhado(item.Id, "Refs · " + pasta.Nome);
    }

    /// <summary>Encaminha pra um capítulo de Estudo (anexa no conteúdo).</summary>
    public static async Task EncaminharParaEstudo(Item item, string estudoTitulo, Secao secao)
    {
        string text = item.TextoAtual;
        string add = "";
        if (item.HasImage) add += $"<img src=\"{PublicUrl(item.ImagePath!)}\" alt=\"\">";
        if (text.Length > 0) add += $"<p>{EscapeHtml(text).Replace("\n", "<br>")}</p>";
        await Patch("ac_study_sections?id=eq." + Uri.EscapeDataString(secao.Id), new JsonObject
        {
            ["content"] = secao.Conteudo + add,
            ["updated_at"] = DateTime.UtcNow.ToString("o")
        });
        await MarcarEncaminhado(item.Id, $"{estudoTitulo} · {secao.Titulo}");
    }

    /// <summary>Encaminha pra um Livro (vira citação). Precisa de texto.</summary>
    public static async Task EncaminharParaLivro(Item item, Destino livro, int? pagina, string? autor)
    {
        await Post("ac_book_quotes", new JsonObject
        {
            ["book_id"] = livro.Id,
            ["text"] = item.TextoAtual,
            ["page"] = pagina,
            ["author"] = string.IsNullOrWhiteSpace(autor) ? null : autor
        });
        await MarcarEncaminhado(item.Id, "Livro · " + livro.Nome);
    }

    private static async Task MarcarEncaminhado(string id, string label)
    {
        await Patch("ac_inbox?id=eq." + Uri.EscapeDataString(id), new JsonObject { ["status"] = "encaminhado", ["forwarded_label"] = label });
    }

    // ---- helpers HTTP ---------------------------------------------------

    private static async Task<int> Contar(string q)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/rest/v1/" + q + "&select=id");
        req.Headers.Add("Prefer", "count=exact");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        var r = await Http.SendAsync(req);
        if (r.Content.Headers.TryGetValues("Content-Range", out var vals))
        {
            var cr = vals.FirstOrDefault() ?? "";
            var slash = cr.IndexOf('/');
            if (slash >= 0 && int.TryParse(cr[(slash + 1)..], out var total)) return total;
        }
        return 0;
    }

    private static async Task Post(string table, JsonObject row)
    {
        var content = new StringContent(row.ToJsonString(), Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/rest/v1/" + table, content);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"{table} falhou: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }

    private static async Task Patch(string q, JsonObject row)
    {
        var content = new StringContent(row.ToJsonString(), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(new HttpMethod("PATCH"), "/rest/v1/" + q) { Content = content };
        var r = await Http.SendAsync(req);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"patch falhou: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }

    private static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
