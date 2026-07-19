using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Zimbar;

/// <summary>Configurações persistidas (posição da barra, do pomodoro e tema).</summary>
public static class Config
{
    private static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zimbar", "settings.json");

    public static double? BarLeft, BarTop, PomoLeft, PomoTop, BarWidth, ViewMax;
    public static double PomoScale = 1.0;
    public static string Theme = "Verde";

    public static void Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return;
            var s = JsonNode.Parse(File.ReadAllText(PathFile)) as JsonObject;
            if (s is null) return;
            BarLeft = s["left"]?.GetValue<double>();
            BarTop = s["top"]?.GetValue<double>();
            PomoLeft = s["pomoLeft"]?.GetValue<double>();
            PomoTop = s["pomoTop"]?.GetValue<double>();
            BarWidth = s["barWidth"]?.GetValue<double>();
            ViewMax = s["viewMax"]?.GetValue<double>();
            PomoScale = s["pomoScale"]?.GetValue<double>() ?? 1.0;
            Theme = s["theme"]?.GetValue<string>() ?? "Verde";
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            var o = new JsonObject { ["theme"] = Theme, ["pomoScale"] = PomoScale };
            if (BarLeft is double bl) o["left"] = bl;
            if (BarTop is double bt) o["top"] = bt;
            if (PomoLeft is double pl) o["pomoLeft"] = pl;
            if (PomoTop is double pt) o["pomoTop"] = pt;
            if (BarWidth is double bw) o["barWidth"] = bw;
            if (ViewMax is double vm) o["viewMax"] = vm;
            File.WriteAllText(PathFile, o.ToJsonString());
        }
        catch { }
    }
}

/// <summary>
/// Temas NEOBRUTALISTAS: papel claro, tinta preta, bordas grossas, sombras duras
/// (sem blur) e blocos de cor vivos. Troca os recursos no nível do Application,
/// então tudo que usa DynamicResource atualiza na hora (barra, ZimNotes, pomodoro).
/// </summary>
public static class ThemeManager
{
    // Cada tema muda o accent e o tom do "papel"; a tinta é sempre preta. (DESIGN.md §2)
    public record Palette(string Accent, string AccentDeep, string Paper,
                          string Ink = "#111111",
                          string TextDim = "#4A4458",
                          string TextDone = "#8B8598",
                          string Dificil = "#FF6B6B",
                          string Media = "#FFDB58",
                          string Facil = "#90EE90");

    public static readonly System.Collections.Generic.Dictionary<string, Palette> Themes = new()
    {
        ["Roxo"] = new("#A78BFA", "#7C3AED", "#E6DBFF"),
        ["Azul"] = new("#6EC1FF", "#1D9BF0", "#CFE8FF"),
        ["Verde"] = new("#6EE7A0", "#16A34A", "#D3F5DC"),
        ["Rosa"] = new("#FF8FC2", "#EC4899", "#FFD9EC"),
        ["Âmbar"] = new("#FFD34D", "#F59E0B", "#FFEDB8"),
    };

    public static void Apply(string name)
    {
        name = name switch
        {
            "Aurora Glass" => "Verde",
            "Noir HUD" or "Orbital Console" => "Roxo",
            _ => name
        };
        if (!Themes.TryGetValue(name, out var p)) { name = "Roxo"; p = Themes["Roxo"]; }
        Config.Theme = name;

        var r = Application.Current.Resources;
        string inkHex = p.Ink;

        r["Accent"] = Brush(p.Accent);
        r["AccentSoft"] = Brush(p.AccentDeep);
        r["AccentColor"] = Col(p.Accent);
        r["GlowColor"] = Col(inkHex);
        r["Zimbar.Brush.Accent"] = Brush(p.Accent);
        r["Zimbar.Brush.AccentSoft"] = Brush(p.AccentDeep);
        r["Zimbar.Color.Accent"] = Col(p.Accent);
        r["Zimbar.Color.Glow"] = Col(inkHex);

        // Tinta: texto sobre papel e sobre blocos de accent — sempre quase-preta.
        r["Ink"] = Brush(inkHex);
        r["TextInk"] = Brush(inkHex);
        r["Zimbar.Brush.Text.OnAccent"] = Brush(inkHex);
        r["TextMain"] = Brush(inkHex);
        r["TextDim"] = Brush(p.TextDim);
        r["TextDone"] = Brush(p.TextDone);
        r["Zimbar.Brush.Text.Primary"] = Brush(inkHex);
        r["Zimbar.Brush.Text.Secondary"] = Brush(p.TextDim);
        r["Zimbar.Brush.Text.Muted"] = Brush(p.TextDone);

        // Superfícies: branco puro sobre papel colorido
        r["Surface"] = Brush("#FFFFFF");
        r["SurfaceHi"] = Brush("#FFFFFF");
        r["ChipBg"] = Brush("#FFFFFF");
        r["ChipBgHover"] = Brush("#2E" + p.Accent[1..]);
        r["CardBg"] = Brush(p.Paper);
        r["Zimbar.Brush.Surface.Glass"] = Brush("#FFFFFF");
        r["Zimbar.Brush.Surface.GlassStrong"] = Brush("#FFFFFF");
        r["Zimbar.Brush.Surface.CardFlat"] = Brush(p.Paper);

        // Blocos vibrantes (paleta de alternância do DESIGN.md §2)
        r["BlockYellow"] = Brush("#FDFD96");
        r["BlockLime"] = Brush("#90EE90");
        r["BlockPink"] = Brush("#FFB2EF");
        r["BlockPurple"] = Brush("#C4A1FF");
        r["BlockBlue"] = Brush("#87CEEB");
        r["BlockCoral"] = Brush("#FFA07A");

        // Fundo do card principal: papel chapado (nada de gradiente)
        var bg = Brush(p.Paper);
        r["CardBgBrush"] = bg;
        r["Zimbar.Brush.Surface.CardCosmic"] = bg;

        // Borda: tinta sólida e grossa
        r["CardBorderBrush"] = Brush(inkHex);
        r["Zimbar.Brush.Border.Card"] = Brush(inkHex);

        // Sombras DURAS (sem blur, deslocadas) — SO pra janela/popups opacos;
        // dentro de conteudo usar Zui.Block (borda dupla), nunca Effect (DESIGN.md §4)
        var hard = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 8, Direction = 315, Opacity = 1, Color = Col(inkHex) };
        hard.Freeze();
        var none = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 0, Opacity = 0 };
        none.Freeze();
        r["CardGlow"] = hard;      // card principal: sombra dura grande
        r["AccentGlow"] = none;    // sem glow em texto/ícone
        r["Zimbar.Effect.Glow.Card"] = hard;
        r["Zimbar.Effect.Glow.Accent"] = none;

        // Plano de hoje — três energias, chapadas
        r["Dificil"] = Brush(p.Dificil);
        r["DificilBg"] = Brush("#2E" + p.Dificil[1..]);
        r["Media"] = Brush(p.Media);
        r["MediaBg"] = Brush("#2E" + p.Media[1..]);
        r["Facil"] = Brush(p.Facil);
        r["FacilBg"] = Brush("#2E" + p.Facil[1..]);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(Col(hex));
        b.Freeze();
        return b;
    }

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
