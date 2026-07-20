using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Zimbar;

/// <summary>
/// Cliente do "contas.pedro" (contaspedro1.netlify.app) — projeto Supabase
/// PRÓPRIO (abdlsipwqckuzaiuujcl), diferente do flowspace do Zimbar. Chave anon
/// já pública no site. Só lê (+ registra gasto) pra alimentar o painel de meta.
/// </summary>
public static class Contas
{
    private const string Url = "https://abdlsipwqckuzaiuujcl.supabase.co";
    private const string Key = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImFiZGxzaXB3cWNrdXphaXV1amNsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk4MjA1ODQsImV4cCI6MjA5NTM5NjU4NH0.pJLcEncJ4nGcrH1KUPEDET9GM3krSWjlARUeaRGnYO0";

    private static readonly HttpClient Http = Create();
    private static HttpClient Create()
    {
        var c = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.Add("apikey", Key);
        c.DefaultRequestHeaders.Add("Authorization", "Bearer " + Key);
        return c;
    }

    private static async Task<JsonArray> Select(string q)
    {
        var r = await Http.GetAsync("/rest/v1/" + q);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonArray ?? new JsonArray();
    }

    /// <summary>Um mês da projeção da planilha: total comprometido + itens.</summary>
    public record MesResumo(int Ano, int Mes, double Total, List<(string Nome, double Valor)> Itens);

    /// <summary>Resultado do painel de meta: quanto ainda pode gastar hoje, etc.</summary>
    public record Snapshot(
        bool Ok, bool TemMeta,
        double DispHoje,      // quanto AINDA pode gastar hoje (orçamento - gasto de hoje)
        double OrcHoje,       // orçamento total do dia
        double GastoHoje,     // já gasto hoje
        double Restante,      // dinheiro livre até a meta
        int DiasRestantes,    // N (inclui hoje)
        DateTime Alvo,        // data da meta
        double Objetivo,
        bool Inviavel,        // nem sem gastar dá pra bater a meta
        bool PeriodoEncerrado,
        List<(DateTime Dia, double Valor, string Nota)> UltimosGastos,
        double SaldoConta,    // saldo atual em conta (pra pré-preencher "atualizar conta")
        double Fatura,        // fatura atual
        List<MesResumo> Proximos6);   // projeção dos próximos 6 meses da planilha

    public static async Task<Snapshot> Carregar()
    {
        try
        {
            var metaT = Select("meta?select=*&limit=1");
            var gastosT = Select("gastos_dia?select=*&order=dia.desc,criado_em.desc&limit=500");
            var comprasT = Select("compras?select=*&order=id.asc");
            var fixosT = Select("fixos?select=*&order=id.asc");
            await Task.WhenAll(metaT, gastosT, comprasT, fixosT);

            var meta = metaT.Result.OfType<JsonObject>().FirstOrDefault();
            if (meta is null)
                return new Snapshot(true, false, 0, 0, 0, 0, 0, DateTime.Now, 0, false, false, new(), 0, 0, new());

            double D(JsonObject o, string k) => o[k] is JsonNode n && double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
            DateTime Dt(string? s) => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : DateTime.Now;

            double objetivo = D(meta, "objetivo");
            double saldoConta = D(meta, "saldo_conta");
            double fatura = D(meta, "fatura");
            double salario = D(meta, "salario");
            int diaPag = (int)D(meta, "dia_pagamento");
            DateTime alvo = DateTime.TryParse(meta["data_alvo"]?.GetValue<string>(), out var a) ? a.Date : DateTime.Now.Date;
            DateTime atualizadoEm = Dt(meta["atualizado_em"]?.GetValue<string>());
            DateTime criadoEm = Dt(meta["criado_em"]?.GetValue<string>());
            DateTime hoje = DateTime.Now.Date;

            // gastos do período (após a última atualização do saldo)
            var gastos = gastosT.Result.OfType<JsonObject>()
                .Select(g => (Dia: DateTime.TryParse(g["dia"]?.GetValue<string>(), out var gd) ? gd.Date : DateTime.MinValue,
                              Valor: D(g, "valor"),
                              Nota: g["nota"]?.GetValue<string>() ?? "",
                              Criado: Dt(g["criado_em"]?.GetValue<string>())))
                .ToList();
            var periodo = gastos.Where(g => g.Criado > atualizadoEm).ToList();
            double gastoHoje = periodo.Where(g => g.Dia == hoje).Sum(g => g.Valor);
            double gastosAntes = periodo.Where(g => g.Dia != hoje).Sum(g => g.Valor);

            // salários que caem entre a atualização e a meta
            double salTotal = SalariosNoPeriodo(salario, diaPag, atualizadoEm.Date, alvo);
            // parcelas + fixos comprometidos nos meses futuros até a meta
            double compTotal = ComprometidoFuturo(comprasT.Result, fixosT.Result, hoje, alvo);

            double disponivelTotal = saldoConta + salTotal - fatura - compTotal - objetivo;
            double restante = disponivelTotal - gastosAntes;
            bool encerrado = alvo < hoje;
            int n = Math.Max(1, (alvo - hoje).Days + 1);
            double orcHoje = restante / n;
            double dispHoje = orcHoje - gastoHoje;

            var ultimos = gastos.Take(6).Select(g => (g.Dia, g.Valor, g.Nota)).ToList();

            // projeção da planilha: próximos 6 meses (a partir do mês atual)
            var proximos = new List<MesResumo>();
            int py = hoje.Year, pm = hoje.Month;
            for (int i = 0; i < 6; i++)
            {
                var itens = ItensMes(comprasT.Result, fixosT.Result, py, pm);
                proximos.Add(new MesResumo(py, pm, itens.Sum(x => x.Valor), itens));
                pm++; if (pm > 12) { pm = 1; py++; }
            }

            return new Snapshot(true, true, dispHoje, orcHoje, gastoHoje, restante, n, alvo,
                objetivo, disponivelTotal < 0, encerrado, ultimos, saldoConta, fatura, proximos);
        }
        catch
        {
            return new Snapshot(false, false, 0, 0, 0, 0, 0, DateTime.Now, 0, false, false, new(), 0, 0, new());
        }
    }

    private static double SalariosNoPeriodo(double salario, int diaPag, DateTime refDay, DateTime alvo)
    {
        if (salario <= 0 || diaPag <= 0) return 0;
        int n = 0;
        int y = refDay.Year, m = refDay.Month;
        while (y < alvo.Year || (y == alvo.Year && m <= alvo.Month))
        {
            int pd = Math.Min(diaPag, DateTime.DaysInMonth(y, m));
            var k = new DateTime(y, m, pd);
            if (k > refDay && k <= alvo) n++;
            m++; if (m > 12) { m = 1; y++; }
        }
        return n * salario;
    }

    private static double ComprometidoFuturo(JsonArray compras, JsonArray fixos, DateTime hoje, DateTime alvo)
    {
        double total = 0;
        int y = hoje.Year, m = hoje.Month;
        m++; if (m > 12) { m = 1; y++; }   // começa no mês seguinte
        while (y < alvo.Year || (y == alvo.Year && m <= alvo.Month))
        {
            total += ParcelasMes(compras, y, m) + FixosMes(fixos, y, m);
            m++; if (m > 12) { m = 1; y++; }
        }
        return total;
    }

    // parcelas que caem no ano/mês (m = 1-12); espelha dates()+getParcelasMes do site (mês base m-1)
    private static double ParcelasMes(JsonArray compras, int y, int mo)
    {
        double D(JsonObject o, string k) => o[k] is JsonNode n && double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        int mIdx = mo - 1;
        double t = 0;
        foreach (var node in compras)
        {
            if (node is not JsonObject c) continue;
            string ds = c["d"]?.GetValue<string>() ?? "";
            var parts = ds.Split('-');
            if (parts.Length < 3 || !int.TryParse(parts[0], out int cy) || !int.TryParse(parts[1], out int cm)) continue;
            int parcelas = (int)D(c, "parcelas");
            double vp = D(c, "vp");
            for (int i = 0; i < parcelas; i++)
            {
                int nm = cm - 1 + i;
                int ny = cy + (int)Math.Floor(nm / 12.0);
                nm = ((nm % 12) + 12) % 12;
                if (ny == y && nm == mIdx) t += vp;
            }
        }
        return t;
    }

    // itens (parcelas + fixos) que caem no ano/mês — espelha ParcelasMes/FixosMes com nome
    private static List<(string Nome, double Valor)> ItensMes(JsonArray compras, JsonArray fixos, int y, int mo)
    {
        double D(JsonObject o, string k) => o[k] is JsonNode n && double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        int mIdx = mo - 1;
        var itens = new List<(string, double)>();
        foreach (var node in compras)
        {
            if (node is not JsonObject c) continue;
            string ds = c["d"]?.GetValue<string>() ?? "";
            var parts = ds.Split('-');
            if (parts.Length < 3 || !int.TryParse(parts[0], out int cy) || !int.TryParse(parts[1], out int cm)) continue;
            int parcelas = (int)D(c, "parcelas");
            double vp = D(c, "vp");
            string nome = c["nome"]?.GetValue<string>() ?? "compra";
            for (int i = 0; i < parcelas; i++)
            {
                int nm = cm - 1 + i;
                int ny = cy + (int)Math.Floor(nm / 12.0);
                nm = ((nm % 12) + 12) % 12;
                if (ny == y && nm == mIdx)
                {
                    string rot = parcelas > 1 ? $"{nome} ({i + 1}/{parcelas})" : nome;
                    itens.Add((rot, vp));
                }
            }
        }
        string fk = $"{y}-{mo:00}";
        foreach (var node in fixos)
        {
            if (node is not JsonObject f) continue;
            string nome = f["nome"]?.GetValue<string>() ?? "fixo";
            double v = D(f, "valor");
            if (f["overrides"] is JsonObject ov && ov[fk] is JsonNode on && double.TryParse(on.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vo))
                v = vo;
            if (v != 0) itens.Add((nome, v));
        }
        return itens.OrderByDescending(x => x.Item2).ToList();
    }

    private static double FixosMes(JsonArray fixos, int y, int mo)
    {
        double D(JsonObject o, string k) => o[k] is JsonNode n && double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        string k = $"{y}-{mo:00}";
        double t = 0;
        foreach (var node in fixos)
        {
            if (node is not JsonObject f) continue;
            if (f["overrides"] is JsonObject ov && ov[k] is JsonNode on && double.TryParse(on.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                t += v;
            else
                t += D(f, "valor");
        }
        return t;
    }

    /// <summary>
    /// Atualiza o saldo em conta e a fatura na meta (igual o "atualizar valores" do site).
    /// Move atualizado_em pra agora, o que zera o período de gastos — o saldo informado
    /// passa a ser o novo ponto de partida do cálculo.
    /// </summary>
    public static async Task AtualizarConta(double saldoConta, double fatura)
    {
        var patch = new JsonObject
        {
            ["saldo_conta"] = saldoConta,
            ["fatura"] = fatura,
            ["atualizado_em"] = DateTime.UtcNow.ToString("o")
        };
        var content = new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(new HttpMethod("PATCH"), "/rest/v1/meta?id=eq.1") { Content = content };
        var r = await Http.SendAsync(req);
        r.EnsureSuccessStatusCode();
    }

    public static string Fmt(double v) => "R$ " + v.ToString("N2", new CultureInfo("pt-BR"));
}
