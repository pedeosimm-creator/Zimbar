using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zimbar;

/// <summary>
/// Cronômetro flutuante do pomodoro. Vive sozinho na tela: arrasta pra mover,
/// clique pausa/retoma, botão direito tem o resto. Avisa pela bandeja.
/// </summary>
public partial class PomoWindow : Window
{
    private static PomoWindow? _instance;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _workMin = 25, _breakMin = 5;
    private TimeSpan _remaining;
    private bool _running, _isBreak;

    public static void Launch(int workMin, int breakMin)
    {
        _instance ??= new PomoWindow();
        _instance.StartCycle(workMin, breakMin);
    }

    private PomoWindow()
    {
        InitializeComponent();
        _timer.Tick += Tick;
        Closed += (_, _) => { _instance = null; ((App)Application.Current).SetTrayText("Zimbar — Ctrl+Alt+Z pra abrir"); };

        if (Config.PomoLeft is double l && Config.PomoTop is double t) { Left = l; Top = t; }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - 190;
            Top = wa.Top + 24;
        }
        ApplyScale();
    }

    private void ApplyScale()
        => Pill.LayoutTransform = new ScaleTransform(Config.PomoScale, Config.PomoScale);

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string t }
            && double.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            Config.PomoScale = s;
            Config.Save();
            ApplyScale();
        }
    }

    private void StartCycle(int workMin, int breakMin)
    {
        _workMin = workMin; _breakMin = breakMin;
        _isBreak = false;
        _remaining = TimeSpan.FromMinutes(_workMin);
        _running = true;
        _timer.Start();
        Show();
        Render();
    }

    private void Tick(object? sender, EventArgs e)
    {
        _remaining -= TimeSpan.FromSeconds(1);
        var app = (App)Application.Current;

        if (_remaining <= TimeSpan.Zero)
        {
            if (!_isBreak)
            {
                _isBreak = true;
                _remaining = TimeSpan.FromMinutes(_breakMin);
                app.Notify("🍅 Pomodoro completo!", $"Boa, Pedro. {_breakMin} min de pausa agora.");
            }
            else
            {
                _isBreak = false;
                _remaining = TimeSpan.FromMinutes(_workMin);
                _running = false;
                _timer.Stop();
                app.Notify("✦ Pausa encerrada", "Clica na pílula quando quiser outro foco.");
            }
        }
        Render();
    }

    private void Render()
    {
        TimerText.Text = _remaining.ToString(@"mm\:ss");
        string phase = _isBreak ? "pausa" : "foco";
        PhaseDot.Fill = _isBreak
            ? (Brush)FindResource("Facil")
            : _running ? (Brush)FindResource("Accent") : (Brush)FindResource("TextDim");
        Pill.Opacity = _running ? 0.88 : 0.55;
        ((App)Application.Current).SetTrayText(_running
            ? $"Zimbar — 🍅 {TimerText.Text} ({phase})"
            : "Zimbar — 🍅 pausado");
    }

    // Clique = pausa/retoma; arrastar = mover; Ctrl+arrastar = redimensionar
    private bool _resizing;
    private double _resizeStartX, _scaleStart;

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ctrl segurado → entra em modo de redimensionar arrastando na horizontal
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _resizing = true;
            _resizeStartX = e.GetPosition(this).X;
            _scaleStart = Config.PomoScale;
            Pill.CaptureMouse();
            e.Handled = true;
            return;
        }

        double l0 = Left, t0 = Top;
        DragMove();
        bool moved = Math.Abs(Left - l0) > 3 || Math.Abs(Top - t0) > 3;

        Config.PomoLeft = Left; Config.PomoTop = Top;
        Config.Save();

        if (!moved) TogglePause();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_resizing) return;
        double dx = e.GetPosition(this).X - _resizeStartX;
        Config.PomoScale = Math.Clamp(_scaleStart + dx / 160.0, 0.6, 2.2);
        ApplyScale();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_resizing) return;
        _resizing = false;
        Pill.ReleaseMouseCapture();
        Config.Save();
    }

    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Config.PomoScale = Math.Clamp(Config.PomoScale + (e.Delta > 0 ? 0.1 : -0.1), 0.6, 2.2);
        ApplyScale();
        Config.Save();
        e.Handled = true;
    }

    private void TogglePause()
    {
        _running = !_running;
        if (_running) _timer.Start(); else _timer.Stop();
        Render();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _running = false; _isBreak = false;
        _remaining = TimeSpan.FromMinutes(_workMin);
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }
}
