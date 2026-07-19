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
    // Linguagem do Acervo: fundo cream fixo, tinta quente, cards paper; o tema só
    // troca o ACCENT (um dos 6 vibrantes do Acervo). (DESIGN.md)
    public record Palette(string Accent, string AccentSoft);

    public static readonly System.Collections.Generic.Dictionary<string, Palette> Themes = new()
    {
        ["Roxo"] = new("#7b61ff", "#d9d1ff"),   // grape
        ["Azul"] = new("#4d7cff", "#cdd9ff"),   // sky
        ["Verde"] = new("#3ec46d", "#c4eed4"),  // leaf
        ["Rosa"] = new("#ff5c8a", "#ffd0de"),   // rose
        ["Âmbar"] = new("#ffc940", "#ffe9ac"),  // sun
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
        // Tokens fixos do Acervo
        const string inkHex = "#161613";     // tinta quente
        const string cream = "#f6f2e7";      // fundo da janela
        const string paper = "#fffdf7";      // superfície de cards
        const string mist = "#e8e1cf";       // hover neutro
        const string dim = "#6f6a5c";        // texto secundário (~ink/60)
        const string done = "#a39b85";       // texto apagado (placeholder do Acervo)

        r["Accent"] = Brush(p.Accent);
        r["AccentSoft"] = Brush(p.AccentSoft);
        r["AccentColor"] = Col(p.Accent);
        r["GlowColor"] = Col(inkHex);
        r["Zimbar.Brush.Accent"] = Brush(p.Accent);
        r["Zimbar.Brush.AccentSoft"] = Brush(p.AccentSoft);
        r["Zimbar.Color.Accent"] = Col(p.Accent);
        r["Zimbar.Color.Glow"] = Col(inkHex);

        r["Ink"] = Brush(inkHex);
        r["TextInk"] = Brush(inkHex);
        r["Zimbar.Brush.Text.OnAccent"] = Brush(inkHex);
        r["TextMain"] = Brush(inkHex);
        r["TextDim"] = Brush(dim);
        r["TextDone"] = Brush(done);
        r["Zimbar.Brush.Text.Primary"] = Brush(inkHex);
        r["Zimbar.Brush.Text.Secondary"] = Brush(dim);
        r["Zimbar.Brush.Text.Muted"] = Brush(done);

        // Superfícies: paper nos cards, cream no fundo, mist no hover neutro
        r["Surface"] = Brush(paper);
        r["SurfaceHi"] = Brush(paper);
        r["ChipBg"] = Brush(paper);
        r["ChipBgHover"] = Brush(mist);
        r["Mist"] = Brush(mist);
        r["Cream"] = Brush(cream);
        r["CardBg"] = Brush(cream);
        r["Zimbar.Brush.Surface.Glass"] = Brush(paper);
        r["Zimbar.Brush.Surface.GlassStrong"] = Brush(paper);
        r["Zimbar.Brush.Surface.CardFlat"] = Brush(paper);

        // Paleta vibrante do Acervo (cheia) + soft (fundo de card)
        r["Sun"] = Brush("#ffc940"); r["SunSoft"] = Brush("#ffe9ac");
        r["Tang"] = Brush("#ff5f35"); r["TangSoft"] = Brush("#ffd3c4");
        r["Leaf"] = Brush("#3ec46d"); r["LeafSoft"] = Brush("#c4eed4");
        r["Sky"] = Brush("#4d7cff"); r["SkySoft"] = Brush("#cdd9ff");
        r["Grape"] = Brush("#7b61ff"); r["GrapeSoft"] = Brush("#d9d1ff");
        r["Rose"] = Brush("#ff5c8a"); r["RoseSoft"] = Brush("#ffd0de");

        // Compat: as chaves Block* antigas apontam pros vibrantes do Acervo
        r["BlockYellow"] = Brush("#ffc940");
        r["BlockLime"] = Brush("#3ec46d");
        r["BlockPink"] = Brush("#ff5c8a");
        r["BlockPurple"] = Brush("#7b61ff");
        r["BlockBlue"] = Brush("#4d7cff");
        r["BlockCoral"] = Brush("#ff5f35");

        // Fundo do card principal (a janela) = cream
        r["CardBgBrush"] = Brush(cream);
        r["Zimbar.Brush.Surface.CardCosmic"] = Brush(cream);

        r["CardBorderBrush"] = Brush(inkHex);
        r["Zimbar.Brush.Border.Card"] = Brush(inkHex);

        // Sombra dura do Acervo: 4px 4px, sem blur — só na janela/popups opacos;
        // dentro de conteúdo usar Zui.Block (borda dupla), nunca Effect (DESIGN.md)
        var hard = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 5.6, Direction = 315, Opacity = 1, Color = Col(inkHex) };
        hard.Freeze();
        var none = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 0, Opacity = 0 };
        none.Freeze();
        r["CardGlow"] = hard;
        r["AccentGlow"] = none;
        r["Zimbar.Effect.Glow.Card"] = hard;
        r["Zimbar.Effect.Glow.Accent"] = none;

        // Plano de hoje — energias do Acervo (tang/sun/leaf) + soft de fundo
        r["Dificil"] = Brush("#ff5f35"); r["DificilBg"] = Brush("#ffd3c4");
        r["Media"] = Brush("#ffc940"); r["MediaBg"] = Brush("#ffe9ac");
        r["Facil"] = Brush("#3ec46d"); r["FacilBg"] = Brush("#c4eed4");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(Col(hex));
        b.Freeze();
        return b;
    }

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
