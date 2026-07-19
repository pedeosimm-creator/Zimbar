using System;
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
}
