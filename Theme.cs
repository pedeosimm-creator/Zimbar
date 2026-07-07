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
    public static string Theme = "Roxo";

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
            Theme = s["theme"]?.GetValue<string>() ?? "Roxo";
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
/// Temas cósmicos. Troca os recursos no nível do Application, então tudo que
/// usa DynamicResource atualiza na hora (barra, ZimNotes, pomodoro).
/// </summary>
public static class ThemeManager
{
    // Estética "nebulosa": fundo espacial profundo, accent neon, brilho suave,
    // vidro translúcido. Cada tema muda o accent e o tom do espaço.
    public record Palette(string Accent, string AccentSoft, string Glow,
                          string Bg1, string Bg2, string Bg3, string Border,
                          string TextMain = "#F1ECFF",
                          string TextDim = "#B4ADD4",
                          string TextDone = "#7F78A2",
                          string Surface = "#14FFFFFF",
                          string SurfaceHi = "#22FFFFFF",
                          string ChipBg = "#18FFFFFF",
                          string ChipBgHover = "#33FFFFFF",
                          double CardGlowOpacity = 0.5,
                          double AccentGlowOpacity = 0.85,
                          string Dificil = "#FF8C6B",
                          string Media = "#FFD98A",
                          string Facil = "#8FF0C4");

    public static readonly System.Collections.Generic.Dictionary<string, Palette> Themes = new()
    {
        ["Roxo"]  = new("#C9A6FF", "#9A7FE8", "#8B5CFF", "#160F22", "#1E1636", "#100A1B", "#6E4FD6"),
        ["Azul"]  = new("#8FD0FF", "#5FA8E8", "#3D9BFF", "#0C1626", "#102237", "#0A0F1C", "#3C74C8"),
        ["Verde"] = new("#8FF0C4", "#5FCFA0", "#34E89C", "#0B1F18", "#0F2C22", "#081711", "#2F9E76"),
        ["Rosa"]  = new("#FFA8DC", "#E87FBB", "#FF5CBE", "#1E1020", "#2C1636", "#160B1B", "#C85CA6"),
        ["Âmbar"] = new("#FFD79A", "#E8B87F", "#FFB35C", "#1E180F", "#2C2314", "#16110A", "#C89A54"),
        ["Noir HUD"] = new("#D7CCFF", "#7E8CFF", "#B9A7FF", "#030407", "#0B0D12", "#020203", "#4D5268",
            TextMain: "#F4F0E8", TextDim: "#A9A8B2", TextDone: "#62636D",
            Surface: "#10FFFFFF", SurfaceHi: "#19FFFFFF", ChipBg: "#0FFFFFFF", ChipBgHover: "#24FFFFFF",
            CardGlowOpacity: 0.24, AccentGlowOpacity: 0.55,
            Dificil: "#FF7A7A", Media: "#E6D7A2", Facil: "#A8F0C8"),
        ["Aurora Glass"] = new("#7CF7D4", "#7CB8FF", "#42F3C8", "#061014", "#102327", "#160E25", "#3DE2D0",
            TextMain: "#F2FFF9", TextDim: "#A8D0CB", TextDone: "#6F928D",
            Surface: "#18FFFFFF", SurfaceHi: "#2AFFFFFF", ChipBg: "#1CFFFFFF", ChipBgHover: "#3AFFFFFF",
            CardGlowOpacity: 0.42, AccentGlowOpacity: 0.72,
            Dificil: "#FF9BA8", Media: "#FFE08A", Facil: "#7CF7D4"),
        ["Orbital Console"] = new("#7CCBFF", "#FFD166", "#48A8FF", "#050912", "#101A25", "#03060B", "#2F8ED6",
            TextMain: "#EAF6FF", TextDim: "#9AB3C7", TextDone: "#5D7083",
            Surface: "#12D5ECFF", SurfaceHi: "#20D5ECFF", ChipBg: "#13FFFFFF", ChipBgHover: "#2AFFFFFF",
            CardGlowOpacity: 0.34, AccentGlowOpacity: 0.68,
            Dificil: "#FF8B72", Media: "#FFD166", Facil: "#74E0B4"),
    };

    public static void Apply(string name)
    {
        if (!Themes.TryGetValue(name, out var p)) { name = "Roxo"; p = Themes["Roxo"]; }
        Config.Theme = name;

        var r = Application.Current.Resources;

        r["Accent"] = Brush(p.Accent);
        r["AccentSoft"] = Brush(p.AccentSoft);
        r["AccentColor"] = Col(p.Accent);
        r["GlowColor"] = Col(p.Glow);
        r["Zimbar.Brush.Accent"] = Brush(p.Accent);
        r["Zimbar.Brush.AccentSoft"] = Brush(p.AccentSoft);
        r["Zimbar.Color.Accent"] = Col(p.Accent);
        r["Zimbar.Color.Glow"] = Col(p.Glow);
        r["Ink"] = Brush("#0C0A12");
        r["TextInk"] = Brush("#120A22");
        r["Zimbar.Brush.Text.OnAccent"] = Brush("#120A22");

        r["TextMain"] = Brush(p.TextMain);
        r["TextDim"] = Brush(p.TextDim);
        r["TextDone"] = Brush(p.TextDone);
        r["Zimbar.Brush.Text.Primary"] = Brush(p.TextMain);
        r["Zimbar.Brush.Text.Secondary"] = Brush(p.TextDim);
        r["Zimbar.Brush.Text.Muted"] = Brush(p.TextDone);

        // Vidro translúcido (branco em alfa baixo por cima do espaço)
        r["Surface"] = Brush(p.Surface);
        r["SurfaceHi"] = Brush(p.SurfaceHi);
        r["ChipBg"] = Brush(p.ChipBg);
        r["ChipBgHover"] = Brush(p.ChipBgHover);
        r["CardBg"] = Brush(p.Bg2);
        r["Zimbar.Brush.Surface.Glass"] = Brush(p.Surface);
        r["Zimbar.Brush.Surface.GlassStrong"] = Brush(p.SurfaceHi);
        r["Zimbar.Brush.Surface.CardFlat"] = Brush(p.Bg2);

        // Tints de categoria (suaves, futuristas)
        r["BlockYellow"] = Brush("#FFD98A");
        r["BlockLime"] = Brush("#9BE8B8");
        r["BlockPink"] = Brush("#FF9FD4");
        r["BlockPurple"] = Brush("#C4A6FF");
        r["BlockBlue"] = Brush("#8FD0FF");
        r["BlockCoral"] = Brush("#FF9E86");

        // Fundo do card principal: gradiente espacial + estrelas (XAML)
        var bg = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Col(p.Bg1), 0),
                new GradientStop(Col(p.Bg2), 0.55),
                new GradientStop(Col(p.Bg3), 1)
            }
        };
        bg.Freeze();
        r["CardBgBrush"] = bg;
        r["Zimbar.Brush.Surface.CardCosmic"] = bg;

        // Borda hairline luminosa (accent → transparente)
        var border = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Col(p.Accent), 0),
                new GradientStop(Col(p.Border), 0.5),
                new GradientStop((Color)ColorConverter.ConvertFromString("#22" + p.Accent[1..]), 1)
            }
        };
        border.Freeze();
        r["CardBorderBrush"] = border;
        r["Zimbar.Brush.Border.Card"] = border;

        // Brilhos suaves (a assinatura futurista)
        r["CardGlow"] = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = p.CardGlowOpacity, Color = Col(p.Glow) };
        r["AccentGlow"] = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = p.AccentGlowOpacity, Color = Col(p.Accent) };
        r["Zimbar.Effect.Glow.Card"] = r["CardGlow"];
        r["Zimbar.Effect.Glow.Accent"] = r["AccentGlow"];

        // Plano de hoje — três energias, com brilho
        r["Dificil"] = Brush(p.Dificil);
        r["DificilBg"] = Brush("#33" + p.Dificil[1..]);
        r["Media"] = Brush(p.Media);
        r["MediaBg"] = Brush("#33" + p.Media[1..]);
        r["Facil"] = Brush(p.Facil);
        r["FacilBg"] = Brush("#33" + p.Facil[1..]);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(Col(hex));
        b.Freeze();
        return b;
    }

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
