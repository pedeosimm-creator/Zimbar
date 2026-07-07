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
            FontFamily = (FontFamily)owner.FindResource(ZTokens.Mono),
            Foreground = (Brush)owner.FindResource(ZTokens.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? ZTokens.SpaceNone,
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

    public static Border GlassCard(
        FrameworkElement owner,
        UIElement body,
        Action? onClick = null,
        Thickness? padding = null,
        Thickness? margin = null)
    {
        var idleBorder = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        var card = new Border
        {
            Background = (Brush)owner.FindResource(ZTokens.Surface),
            BorderBrush = idleBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = ZTokens.RadiusLg,
            Padding = padding ?? new Thickness(15, 12, 15, 12),
            Margin = margin ?? new Thickness(0, 0, 0, 10),
            Child = body
        };
        if (onClick is not null)
        {
            card.Cursor = Cursors.Hand;
            card.ToolTip = "clica pra abrir a aba";
            card.MouseEnter += (_, _) => card.BorderBrush = (Brush)owner.FindResource(ZTokens.Accent);
            card.MouseLeave += (_, _) => card.BorderBrush = idleBorder;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && fe.Cursor == Cursors.Hand && !ReferenceEquals(fe, card)) return;
                onClick();
                e.Handled = true;
            };
        }
        return card;
    }

    public static Border StatCard(FrameworkElement owner, string title, UIElement body, Action? onClick = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(HudLabel(owner, title));
        sp.Children.Add(body);
        return GlassCard(owner, sp, onClick);
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

        btn.Click += (_, _) =>
        {
            btn.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            if (editor is Grid g && g.Children.Count > 0 && g.Children[0] is TextBox tb)
            {
                tb.Focus();
                tb.PreviewKeyDown += (_, e2) =>
                {
                    if (e2.Key == Key.Escape || (e2.Key == Key.Enter && tb.Text.Trim().Length == 0))
                    {
                        editor.Visibility = Visibility.Collapsed;
                        btn.Visibility = Visibility.Visible;
                        e2.Handled = true;
                    }
                };
            }
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
