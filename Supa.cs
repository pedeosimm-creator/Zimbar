using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Zimbar;

/// <summary>
/// Cliente mínimo do Supabase (REST/PostgREST) para o projeto flowspace —
/// o mesmo banco que alimenta coisadepedro.netlify.app. A chave anon já é
/// pública no código-fonte do site.
/// </summary>
public static class Supa
{
    private const string Url = "https://fautswjwioiviqvpgsrw.supabase.co";
    private const string Key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImZhdXRzd2p3aW9pdmlxdnBnc3J3Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4ODkxNzgsImV4cCI6MjA5NTQ2NTE3OH0.99cWW-JA8-fH_wj_tqNLdpgubhM98UleH-0D5YaENM4";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.Add("apikey", Key);
        c.DefaultRequestHeaders.Add("Authorization", "Bearer " + Key);
        return c;
    }

    public static async Task<JsonArray> Select(string pathAndQuery)
    {
        var r = await Http.GetAsync("/rest/v1/" + pathAndQuery);
        r.EnsureSuccessStatusCode();
        var json = await r.Content.ReadAsStringAsync();
        return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
    }

    public static async Task Insert(string table, JsonObject row)
    {
        var content = new StringContent(row.ToJsonString(), Encoding.UTF8, "application/json");
        var r = await Http.PostAsync("/rest/v1/" + table, content);
        r.EnsureSuccessStatusCode();
    }

    public static async Task Update(string table, string filterQuery, JsonObject patch)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, $"/rest/v1/{table}?{filterQuery}")
        {
            Content = new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var r = await Http.SendAsync(req);
        r.EnsureSuccessStatusCode();
    }

    public static async Task Delete(string table, string filterQuery)
    {
        var r = await Http.DeleteAsync($"/rest/v1/{table}?{filterQuery}");
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Upsert em app_kv (chave/valor que o site usa pro plano de hoje etc.).</summary>
    public static async Task SetKv(string k, string v)
    {
        var row = new JsonObject { ["k"] = k, ["v"] = v, ["updated_at"] = DateTime.UtcNow.ToString("o") };
        var req = new HttpRequestMessage(HttpMethod.Post, "/rest/v1/app_kv")
        {
            Content = new StringContent(row.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Prefer", "resolution=merge-duplicates");
        var r = await Http.SendAsync(req);
        r.EnsureSuccessStatusCode();
    }

    public static async Task<string?> GetKv(string k)
    {
        var rows = await Select($"app_kv?k=eq.{Uri.EscapeDataString(k)}&select=v");
        return rows.Count > 0 ? rows[0]?["v"]?.GetValue<string>() : null;
    }

    /// <summary>Id texto no mesmo espírito dos ids do site (base36 aleatório).</summary>
    public static string NewId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rnd = Random.Shared;
        var sb = new StringBuilder(12);
        for (int i = 0; i < 12; i++) sb.Append(chars[rnd.Next(chars.Length)]);
        return sb.ToString();
    }
}
