using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Zimbar;

/// <summary>
/// As cores e as medidas do ZimNotes, iguais às da versão de celular.
/// Ficam aqui em código, e não no App.xaml, de propósito: o dicionário de
/// lá é do design antigo do Zimbar e as janelas de nota não seguem mais ele.
/// </summary>
public static class Paleta
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>O papel da nota: cinza claríssimo, pra o cartão branco da
    /// lista ter de quem se destacar. Branco sobre branco não separa nada.</summary>
    public static readonly Brush Papel    = B("#F4F5F9");
    public static readonly Brush Fundo    = B("#F3F4F8");
    public static readonly Brush FundoBaixo = B("#E7EAF0");
    public static readonly Brush Cartao   = B("#FFFFFF");
    public static readonly Brush Tinta    = B("#14161B");
    public static readonly Brush Tinta2   = B("#3A3F49");
    public static readonly Brush Fraca    = B("#858C99");
    public static readonly Brush Fraquinha = B("#A8AEB9");
    public static readonly Brush Linha    = B("#E6E8EE");
    public static readonly Brush Acento   = B("#CB4F2F");
    public static readonly Brush TintaNota = B("#22262E");

    /// <summary>O degradê de papel que a versão de celular usa no fundo.</summary>
    public static Brush FundoDaJanela()
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0),
            EndPoint = new Point(0.85, 1)
        };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F3F4F8"), 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#E7EAF0"), 1));
        g.Freeze();
        return g;
    }

    /// <summary>Sombra macia e larga; nada da sombra dura do desenho antigo.</summary>
    public static DropShadowEffect Sombra(double desfoque = 22, double opacidade = 0.13)
    {
        var s = new DropShadowEffect
        {
            BlurRadius = desfoque,
            ShadowDepth = 5,
            Direction = 270,
            Opacity = opacidade,
            Color = (Color)ColorConverter.ConvertFromString("#161B2C")
        };
        s.Freeze();
        return s;
    }

    public const double Raio = 16;
    public const double RaioGrande = 20;
    public static readonly FontFamily Fonte = new("Segoe UI Variable Text, Segoe UI");
}
