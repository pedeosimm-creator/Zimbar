using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Zimbar;

public enum ZButtonKind
{
    Chip,
    Primary,
    Nav,
    Ghost
}

public static class ZTokens
{
    public const string Body = "Body";
    public const string Display = "Display";
    public const string Mono = "Mono";
    public const string Accent = "Accent";
    public const string AccentSoft = "AccentSoft";
    public const string TextInk = "TextInk";
    public const string TextMain = "TextMain";
    public const string TextDim = "TextDim";
    public const string TextDone = "TextDone";
    public const string Surface = "Surface";
    public const string SurfaceHi = "SurfaceHi";
    public const string CardBg = "CardBg";
    public const string CardBorderBrush = "CardBorderBrush";
    public const string Chip = "Chip";
    public const string PrimaryBtn = "PrimaryBtn";
    public const string NavBtn = "NavBtn";
    public const string GhostItem = "GhostItem";
    public const string InlineAdd = "InlineAdd";

    public static readonly Thickness SpaceNone = new(0);
    public static readonly Thickness SpaceXs = new(4);
    public static readonly Thickness SpaceSm = new(8);
    public static readonly Thickness SpaceMd = new(12);
    public static readonly Thickness SpaceLg = new(16);

    public static readonly CornerRadius RadiusSm = new(9);
    public static readonly CornerRadius RadiusMd = new(10);
    public static readonly CornerRadius RadiusLg = new(14);
}

public static class Zui
{
    public static Button Button(
        FrameworkElement owner,
        object content,
        ZButtonKind kind = ZButtonKind.Chip,
        RoutedEventHandler? onClick = null,
        string? tooltip = null)
    {
        var b = new Button
        {
            Style = (Style)owner.FindResource(StyleKey(kind)),
            Content = content,
            ToolTip = tooltip
        };
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    public static TextBlock HudLabel(FrameworkElement owner, string text, Thickness? margin = null)
        => new()
        {
            Text = text.ToUpper(new CultureInfo("pt-BR")),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            FontFamily = (FontFamily)owner.FindResource(ZTokens.Mono),
            Foreground = (Brush)owner.FindResource("Ink"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? ZTokens.SpaceNone,
            // folga de 1px: com UseLayoutRounding a altura arredonda pra baixo e
            // come o topo das maiusculas nessas fontes pequenas
            Padding = new Thickness(0, 1, 0, 1),
            TextWrapping = TextWrapping.Wrap
        };

    public static TextBlock SectionLabel(FrameworkElement owner, string text)
        => HudLabel(owner, text, new Thickness(4, 8, 0, 6));

    public static TextBlock DimText(FrameworkElement owner, string text, Thickness? margin = null)
        => new()
        {
            Text = text,
            FontSize = 12.5,
            Margin = margin ?? new Thickness(8, 2, 0, 2),
            Foreground = (Brush)owner.FindResource(ZTokens.TextDim),
            TextWrapping = TextWrapping.Wrap
        };

    public static TextBlock BodyText(
        FrameworkElement owner,
        string text,
        double fontSize = 13.5,
        string brushKey = ZTokens.TextMain,
        FontWeight? weight = null,
        Thickness? margin = null)
        => new()
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = (Brush)owner.FindResource(brushKey),
            Margin = margin ?? ZTokens.SpaceNone,
            TextWrapping = TextWrapping.Wrap
        };

    /// <summary>Cores SOFT do Acervo pros cards alternarem de fundo. i cicla a paleta.</summary>
    private static readonly string[] TintKeys =
        { "SunSoft", "LeafSoft", "GrapeSoft", "RoseSoft", "SkySoft", "TangSoft" };

    /// <summary>Versão CHEIA (badges de ícone), mesma ordem.</summary>
    private static readonly string[] FullKeys =
        { "Sun", "Leaf", "Grape", "Rose", "Sky", "Tang" };

    public static Brush Tint(FrameworkElement owner, int i)
        => (Brush)owner.FindResource(TintKeys[((i % TintKeys.Length) + TintKeys.Length) % TintKeys.Length]);

    public static Brush TintFull(FrameworkElement owner, int i)
        => (Brush)owner.FindResource(FullKeys[((i % FullKeys.Length) + FullKeys.Length) % FullKeys.Length]);

    /// <summary>Badge de ícone do Acervo: quadrado colorido cheio, borda de tinta, radius 12.</summary>
    public static Border IconBadge(FrameworkElement owner, string glyph, int tintIndex, double size = 34)
        => new()
        {
            Width = size, Height = size,
            Background = TintFull(owner, tintIndex),
            BorderBrush = (Brush)owner.FindResource("Ink"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(9),
            Child = new TextBlock
            {
                Text = glyph, FontSize = size * 0.5,
                Foreground = (Brush)owner.FindResource("Ink"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

    /// <summary>
    /// BLOCO neobrutal (DESIGN.md §4): sombra dura via BORDA DUPLA — um bloco de
    /// tinta atrás, o bloco de cor na frente. Nunca DropShadowEffect (fantasma).
    /// </summary>
    public static Grid Block(
        FrameworkElement owner,
        UIElement body,
        Brush? background = null,
        Action? onClick = null,
        Thickness? padding = null,
        Thickness? margin = null,
        double shadow = 4,
        double radius = 14)
    {
        var ink = (Brush)owner.FindResource("Ink");
        var face = new Border
        {
            Background = background ?? (Brush)owner.FindResource(ZTokens.Surface),
            BorderBrush = ink,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(15, 12, 15, 12),
            Margin = new Thickness(0, 0, shadow, shadow),
            Child = body
        };
        var sombra = new Border
        {
            Background = ink,
            CornerRadius = new CornerRadius(radius),
            Margin = new Thickness(shadow, shadow, 0, 0)
        };
        var host = new Grid
        {
            Margin = margin ?? new Thickness(0, 0, 0, 10),
            SnapsToDevicePixels = true,
            Children = { sombra, face }
        };
        if (onClick is not null)
        {
            host.Cursor = Cursors.Hand;
            // Hover: só realça a borda (sem translate — translate negativo era cortado
            // pelo ScrollViewer e "comia" o canto do card, bug do print 5)
            host.MouseEnter += (_, _) => face.BorderBrush = (Brush)owner.FindResource("Accent");
            host.MouseLeave += (_, _) => face.BorderBrush = ink;
            host.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && fe.Cursor == Cursors.Hand && !ReferenceEquals(fe, host) && !ReferenceEquals(fe, face)) return;
                onClick();
                e.Handled = true;
            };
        }
        return host;
    }

    /// <summary>Compat: os call sites antigos de card continuam funcionando, agora como Block.</summary>
    public static FrameworkElement GlassCard(
        FrameworkElement owner,
        UIElement body,
        Action? onClick = null,
        Thickness? padding = null,
        Thickness? margin = null,
        int? tintIndex = null)
        => Block(owner, body,
            background: tintIndex is int ti ? Tint(owner, ti) : null,
            onClick: onClick, padding: padding, margin: margin);

    /// <summary>TAG de seção (DESIGN.md §5): mono maiúscula clara em bloquinho de tinta.</summary>
    public static Border Tag(FrameworkElement owner, string text, Brush? bg = null, Brush? fg = null)
        => new()
        {
            Background = bg ?? (Brush)owner.FindResource("Ink"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text.ToUpper(new CultureInfo("pt-BR")),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)owner.FindResource(ZTokens.Mono),
                Foreground = fg ?? Brushes.White
            }
        };

    public static FrameworkElement StatCard(FrameworkElement owner, string title, UIElement body, Action? onClick = null, int? tintIndex = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(Tag(owner, title));
        sp.Children.Add(body);
        return GlassCard(owner, sp, onClick, tintIndex: tintIndex);
    }

    public static FrameworkElement InlineAddBox(
        FrameworkElement owner,
        string placeholder,
        Func<string, Task> onEnter,
        Action<string, bool>? showStatus = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var tb = new TextBox { Style = (Style)owner.FindResource(ZTokens.InlineAdd) };
        var hint = new TextBlock
        {
            Text = placeholder,
            FontSize = 12,
            Foreground = (Brush)owner.FindResource(ZTokens.TextDone),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0),
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        tb.TextChanged += (_, _) => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = tb.Text.Trim();
            if (text.Length == 0) return;
            e.Handled = true;
            try
            {
                await onEnter(text);
                tb.Clear();
                showStatus?.Invoke("✓ adicionado", false);
            }
            catch (Exception ex)
            {
                showStatus?.Invoke("⚠ " + ex.Message, true);
            }
        };
        grid.Children.Add(tb);
        grid.Children.Add(hint);
        return grid;
    }

    public static FrameworkElement RevealAdd(
        FrameworkElement owner,
        string label,
        string placeholder,
        Func<string, Task> onEnter,
        Action<string, bool>? showStatus = null)
    {
        var host = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var btn = Button(owner, label);
        btn.FontSize = 11;
        btn.Padding = new Thickness(10, 5, 10, 5);
        btn.Margin = ZTokens.SpaceNone;
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Background = Brushes.Transparent;

        var editor = InlineAddBox(owner, placeholder, onEnter, showStatus);
        editor.Visibility = Visibility.Collapsed;
        editor.Margin = ZTokens.SpaceNone;

        void Fechar()
        {
            editor.Visibility = Visibility.Collapsed;
            btn.Visibility = Visibility.Visible;
        }

        // Handlers presos UMA vez: antes eram reanexados a cada clique no botao.
        var caixa = editor is Grid g && g.Children.Count > 0 ? g.Children[0] as TextBox : null;
        if (caixa is not null)
        {
            caixa.PreviewKeyDown += (_, e2) =>
            {
                if (e2.Key == Key.Escape || (e2.Key == Key.Enter && caixa.Text.Trim().Length == 0))
                {
                    caixa.Text = "";
                    Fechar();
                    e2.Handled = true;
                }
            };
            // Clicou fora: a barra se recolhe em vez de ficar aberta pra sempre.
            caixa.LostKeyboardFocus += (_, _) =>
            {
                if (editor.Visibility != Visibility.Visible) return;
                caixa.Text = "";
                Fechar();
            };
        }

        btn.Click += (_, _) =>
        {
            btn.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            caixa?.Focus();
        };

        host.Children.Add(btn);
        host.Children.Add(editor);
        return host;
    }

    private static string StyleKey(ZButtonKind kind) => kind switch
    {
        ZButtonKind.Primary => ZTokens.PrimaryBtn,
        ZButtonKind.Nav => ZTokens.NavBtn,
        ZButtonKind.Ghost => ZTokens.GhostItem,
        _ => ZTokens.Chip
    };
}
