using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Zimbar;

/// <summary>
/// Seletor de região da tela estilo "print": congela a tela num overlay,
/// o usuário arrasta um retângulo, e devolve os bytes PNG da área escolhida.
/// Retorna null se cancelar (Esc / botão direito).
/// </summary>
public sealed class RegionCapture : Window
{
    private Drawing.Bitmap _full;              // screenshot da tela virtual (pixels físicos)
    private readonly Canvas _canvas;
    private readonly System.Windows.Shapes.Rectangle _sel;
    private Point _start;
    private bool _dragging;
    private double _scaleX = 1, _scaleY = 1;   // DIP -> pixel

    public byte[]? ResultPng { get; private set; }

    public static byte[]? Capturar()
    {
        var w = new RegionCapture();
        w.ShowDialog();
        return w.ResultPng;
    }

    private RegionCapture()
    {
        double vw = SystemParameters.VirtualScreenWidth;
        double vh = SystemParameters.VirtualScreenHeight;
        double vx = SystemParameters.VirtualScreenLeft;
        double vy = SystemParameters.VirtualScreenTop;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Left = vx; Top = vy; Width = vw; Height = vh;
        Cursor = Cursors.Cross;
        Title = "Capturar";

        // screenshot da tela virtual inteira (usa pixels físicos)
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        _full = new Drawing.Bitmap(Math.Max(1, vs.Width), Math.Max(1, vs.Height));
        using (var g = Drawing.Graphics.FromImage(_full))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, _full.Size);

        SourceInitialized += (_, _) =>
        {
            var s = VisualTreeHelper.GetDpi(this);
            _scaleX = s.DpiScaleX; _scaleY = s.DpiScaleY;
        };

        _canvas = new Canvas();
        _canvas.Children.Add(new Image { Source = ToSource(_full), Stretch = Stretch.Fill, Width = vw, Height = vh });
        _canvas.Children.Add(new System.Windows.Shapes.Rectangle { Width = vw, Height = vh, Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x11, 0x11, 0x11)) });
        var dica = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x13)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7),
            Child = new TextBlock { Text = "arraste pra capturar  ·  Esc cancela", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White }
        };
        Canvas.SetLeft(dica, 24); Canvas.SetTop(dica, 24);
        _canvas.Children.Add(dica);

        _sel = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xff, 0xc9, 0x40)),
            StrokeThickness = 2.5, Fill = Brushes.Transparent, Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_sel);
        Content = _canvas;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonDown += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _dragging = true;
        _sel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sel, _start.X); Canvas.SetTop(_sel, _start.Y);
        _sel.Width = 0; _sel.Height = 0;
        CaptureMouse();
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_canvas);
        Canvas.SetLeft(_sel, Math.Min(p.X, _start.X));
        Canvas.SetTop(_sel, Math.Min(p.Y, _start.Y));
        _sel.Width = Math.Abs(p.X - _start.X);
        _sel.Height = Math.Abs(p.Y - _start.Y);
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var p = e.GetPosition(_canvas);
        double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
        double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
        if (w < 6 || h < 6) { Cancel(); return; }

        int px = (int)Math.Round(x * _scaleX), py = (int)Math.Round(y * _scaleY);
        int pw = (int)Math.Round(w * _scaleX), ph = (int)Math.Round(h * _scaleY);
        px = Math.Clamp(px, 0, _full.Width - 1); py = Math.Clamp(py, 0, _full.Height - 1);
        pw = Math.Clamp(pw, 1, _full.Width - px); ph = Math.Clamp(ph, 1, _full.Height - py);

        using var crop = new Drawing.Bitmap(pw, ph);
        using (var g = Drawing.Graphics.FromImage(crop))
            g.DrawImage(_full, new Drawing.Rectangle(0, 0, pw, ph), new Drawing.Rectangle(px, py, pw, ph), Drawing.GraphicsUnit.Pixel);
        using var ms = new MemoryStream();
        crop.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ResultPng = ms.ToArray();
        Close();
    }

    private void Cancel() { ResultPng = null; Close(); }

    private static BitmapSource ToSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _full?.Dispose();
    }
}
