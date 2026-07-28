using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Zimbar;

/// <summary>
/// Pomodoro numa janelinha à parte: continua contando com o Zimbar fechado,
/// fica onde você largar e não rouba a frente. Clique no tempo pausa/segue.
/// </summary>
public class PomoWindow : Window
{
    private static PomoWindow? _aberta;

    private static readonly (string Rot, int Min)[] Modos =
    {
        ("foco", 25), ("pausa", 5), ("descanso", 15)
    };

    private readonly TextBlock _tempo;
    private readonly TextBlock _modo;
    private readonly StackPanel _chips;
    private readonly DispatcherTimer _tique = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _restam = 25 * 60;
    private int _escolhido;
    private bool _rodando;

    public static void Abrir()
    {
        if (_aberta is not null) { _aberta.Activate(); return; }
        _aberta = new PomoWindow();
        _aberta.Closed += (_, _) => _aberta = null;
        _aberta.Show();
        _aberta.Activate();
    }

    private PomoWindow()
    {
        Title = "Pomodoro";
        Width = 232; Height = 178;
        MinWidth = 200; MinHeight = 150;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Topmost = false;
        Cantos.ArredondarQuandoAbrir(this);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 28;
        Top = wa.Bottom - Height - 28;

        var tinta = new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xF0));
        var fraca = new SolidColorBrush(Color.FromRgb(0x95, 0x9B, 0xAB));
        var brasa = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x5C));

        // topo: só arrasto e fechar
        var fechar = new Button
        {
            Content = "✕", FontSize = 11, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = fraca, Padding = new Thickness(8, 3, 9, 3)
        };
        fechar.Click += (_, _) => Close();
        var topo = new DockPanel { Height = 28, Cursor = Cursors.SizeAll };
        DockPanel.SetDock(fechar, Dock.Right);
        topo.Children.Add(fechar);
        var pega = new Border
        {
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = "◔ pomodoro", FontSize = 10.5, Foreground = fraca,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(13, 0, 0, 0)
            }
        };
        pega.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        topo.Children.Add(pega);

        _tempo = new TextBlock
        {
            Text = "25:00", FontSize = 40, FontWeight = FontWeights.SemiBold,
            Foreground = tinta, HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 0)
        };
        _tempo.MouseLeftButtonDown += (_, _) => Alternar();

        _modo = new TextBlock
        {
            Text = "clica pra começar", FontSize = 10.5, Foreground = fraca,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0)
        };

        _chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 11, 0, 0)
        };
        for (int i = 0; i < Modos.Length; i++)
        {
            int qual = i;
            var b = new Button
            {
                Content = Modos[i].Rot, FontSize = 10.5, Cursor = Cursors.Hand,
                Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(10, 3, 10, 4),
                BorderThickness = new Thickness(1), Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x2B, 0x34)),
                Foreground = i == 0 ? brasa : fraca
            };
            b.Click += (_, _) => Escolher(qual);
            _chips.Children.Add(b);
        }

        var corpo = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        corpo.Children.Add(_tempo);
        corpo.Children.Add(_modo);
        corpo.Children.Add(_chips);

        var layout = new DockPanel();
        DockPanel.SetDock(topo, Dock.Top);
        layout.Children.Add(topo);
        layout.Children.Add(corpo);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C)),
            CornerRadius = new CornerRadius(12),
            Child = layout
        };

        _tique.Tick += (_, _) =>
        {
            _restam--;
            if (_restam <= 0)
            {
                Parar();
                _restam = Modos[_escolhido].Min * 60;
                (Application.Current as App)?.Notify("Pomodoro", Modos[_escolhido].Rot + " terminou.");
                _modo.Text = "acabou — clica pra recomeçar";
            }
            Mostrar();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Space) { Alternar(); e.Handled = true; }
        };
        Mostrar();
    }

    private void Escolher(int qual)
    {
        _escolhido = qual;
        Parar();
        _restam = Modos[qual].Min * 60;
        var brasa = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x5C));
        var fraca = new SolidColorBrush(Color.FromRgb(0x95, 0x9B, 0xAB));
        for (int i = 0; i < _chips.Children.Count; i++)
            ((Button)_chips.Children[i]).Foreground = i == qual ? brasa : fraca;
        _modo.Text = "clica pra começar";
        Mostrar();
    }

    private void Alternar()
    {
        if (_rodando) { Parar(); _modo.Text = "pausado"; }
        else { _tique.Start(); _rodando = true; _modo.Text = Modos[_escolhido].Rot + " correndo"; }
    }

    private void Parar()
    {
        _tique.Stop();
        _rodando = false;
    }

    private void Mostrar()
    {
        _tempo.Text = $"{_restam / 60:00}:{_restam % 60:00}";
        (Application.Current as App)?.SetTrayText(
            _rodando ? $"Zimbar — {Modos[_escolhido].Rot} {_tempo.Text}" : "Zimbar — Ctrl+Alt+Z pra abrir");
    }
}
