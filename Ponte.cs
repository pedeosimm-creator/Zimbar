using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Zimbar;

/// <summary>
/// A conversa entre a interface (JavaScript) e o Windows (C#).
///
/// A interface manda {acao:"..."} por postMessage. Quando espera resposta,
/// vem junto um {pedido:N} e a volta sai como {resposta:N, ...}. O caminho
/// contrário são eventos: {evento:"capturado"} e afins.
///
/// Aqui só entra o que o navegador não faz sozinho.
/// </summary>
public class Ponte
{
    private readonly BarWindow _janela;
    private readonly CoreWebView2 _web;

    public Ponte(BarWindow janela, CoreWebView2 web)
    {
        _janela = janela;
        _web = web;
    }

    public async void Receber(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return;

        JsonObject? m;
        try { m = JsonNode.Parse(bruto) as JsonObject; }
        catch { return; }
        if (m is null) return;

        string acao = m["acao"]?.GetValue<string>() ?? "";
        int? pedido = m["pedido"] is JsonNode pn && int.TryParse(pn.ToString(), out var pv) ? pv : null;

        try
        {
            switch (acao)
            {
                case "arrastar":
                    _janela.ArrastarJanela();
                    break;

                case "redimensionar":
                    _janela.RedimensionarJanela(m["borda"]?.GetValue<string>() ?? "");
                    break;

                case "minimizar":
                    _janela.CollapseBar();
                    break;

                case "maximizar":
                    _janela.AlternarMaximizada();
                    break;

                case "fechar":
                    _janela.CollapseBar();
                    break;

                case "abrirLink":
                    AbrirNoNavegador(m["url"]?.GetValue<string>() ?? "");
                    break;

                case "abrirNotas":
                    NotesWindow.Open();
                    break;

                case "pomodoro":
                    PomoWindow.Abrir();
                    break;

                case "noticias":
                    await Noticias(m["secao"]?.GetValue<string>() ?? "destaques",
                                   m["forcar"]?.GetValue<bool>() ?? false, pedido);
                    return;
            }

            if (pedido is int p) Responder(new JsonObject { ["resposta"] = p, ["ok"] = true });
        }
        catch (Exception ex)
        {
            if (pedido is int p)
                Responder(new JsonObject { ["resposta"] = p, ["erro"] = ex.Message });
        }
    }

    /// <summary>As seções da interface e o termo de busca do feed.</summary>
    private static readonly Dictionary<string, string> Secoes = new()
    {
        ["destaques"] = "brasil",
        ["mundo"] = "mundo",
        ["tecnologia"] = "tecnologia",
        ["negocios"] = "economia",
        ["esportes"] = "esporte futebol",
        ["entretenimento"] = "famosos celebridades"
    };

    private async Task Noticias(string secao, bool forcar, int? pedido)
    {
        // seção que você criou: o próprio nome vira a busca
        string busca = Secoes.TryGetValue(secao, out var q) ? q : secao;
        var itens = new JsonArray();
        try
        {
            foreach (var n in await News.Fetch(busca, forcar))
                itens.Add(new JsonObject
                {
                    ["t"] = n.Title,
                    ["f"] = n.Source,
                    ["h"] = Quando(n.When),
                    ["img"] = n.Image,
                    ["link"] = n.Link
                });
        }
        catch (Exception ex)
        {
            if (pedido is int pe)
                Responder(new JsonObject { ["resposta"] = pe, ["erro"] = ex.Message });
            return;
        }
        if (pedido is int p)
            Responder(new JsonObject { ["resposta"] = p, ["itens"] = itens });
    }

    private static string Quando(DateTime q)
    {
        if (q == default) return "";
        var d = DateTime.Now - q;
        if (d.TotalMinutes < 60) return $"há {Math.Max(1, (int)d.TotalMinutes)}min";
        if (d.TotalHours < 24) return $"há {(int)d.TotalHours}h";
        return $"há {(int)d.TotalDays}d";
    }

    private void Responder(JsonObject o)
    {
        try { _web.PostWebMessageAsString(o.ToJsonString()); } catch { }
    }

    public static void AbrirNoNavegador(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch { }
    }
}
