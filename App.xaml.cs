using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Zimbar;

/// <summary>
/// App roda na bandeja (sem janela principal). Registra o hotkey global
/// Ctrl+Alt+Z e alterna a barra flutuante.
/// </summary>
public partial class App : Application
{
    private WinForms.NotifyIcon? _tray;
    private HwndSource? _hotkeySource;
    private BarWindow? _bar;
    private Mutex? _singleInstance;

    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_NOTES = 2;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const uint VK_Z = 0x5A;
    private const uint VK_D = 0x44;

    protected override void OnStartup(StartupEventArgs e)
    {
        WinForms.Application.SetHighDpiMode(WinForms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Zimbar.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        // NÃO definir um AppUserModelID explícito: os atalhos (área de trabalho/inicializar/
        // fixado) usam a identidade derivada do caminho do exe. Se o processo usasse uma
        // identidade custom, a janela minimizada não agruparia com o atalho e apareceriam
        // DOIS botões de Zimbar na barra de tarefas.
        Config.Load();
        ThemeManager.Apply(Config.Theme);
        SetupTray();
        SetupHotkey();

        // Modo de teste: abre a barra direto na inicialização.
        if (Array.Exists(e.Args, a => a == "--show"))
            ToggleBar();
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = CreateZIcon(),
            Visible = true,
            Text = "Zimbar — Ctrl+Alt+Z pra abrir"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir Zimbar  (Ctrl+Alt+Z)", null, (_, _) => ToggleBar());
        menu.Items.Add("Abrir ZimNotes  (Ctrl+Alt+D)", null, (_, _) => NotesWindow.Open());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleBar();

        _tray.BalloonTipTitle = "Zimbar ativa";
        _tray.BalloonTipText = "Ctrl+Alt+Z pra abrir o cinto de ferramentas.";
        _tray.ShowBalloonTip(3000);
    }

    /// <summary>Desenha o ícone da bandeja em runtime: "Z" laranja em fundo escuro.</summary>
    private static Drawing.Icon CreateZIcon()
    {
        // Usa o logo oficial embutido; se falhar, desenha um Z simples.
        try
        {
            var uri = new Uri("pack://application:,,,/assets/Zimbar.ico");
            var info = GetResourceStream(uri);
            if (info?.Stream is System.IO.Stream s) return new Drawing.Icon(s, new Drawing.Size(32, 32));
        }
        catch { }

        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(30, 20, 60));
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, 31, 31);
            g.FillPath(bg, path);
            using var font = new Drawing.Font("Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fg = new Drawing.SolidBrush(Drawing.Color.FromArgb(201, 166, 255));
            var fmt = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("Z", font, fg, new Drawing.RectangleF(0, 0, 32, 32), fmt);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void SetupHotkey()
    {
        var p = new HwndSourceParameters("ZimbarHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _hotkeySource = new HwndSource(p);
        _hotkeySource.AddHook(WndProc);

        if (!RegisterHotKey(_hotkeySource.Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_Z))
        {
            _tray?.ShowBalloonTip(4000, "Zimbar",
                "Não consegui registrar Ctrl+Alt+Z (outro app está usando?). Use o ícone da bandeja.",
                WinForms.ToolTipIcon.Warning);
        }

        // Ctrl+Alt+D abre direto o ZimNotes.
        RegisterHotKey(_hotkeySource.Handle, HOTKEY_NOTES, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_TOGGLE) { ToggleBar(); handled = true; }
            else if (id == HOTKEY_NOTES) { NotesWindow.Open(); handled = true; }
        }
        return IntPtr.Zero;
    }

    private void ToggleBar()
    {
        _bar ??= new BarWindow();
        if (!_bar.IsVisible)
            _bar.ShowBar();
        else if (_bar.IsCollapsed)
            _bar.ExpandBar();   // se está como aba, o hotkey reabre
        else
            _bar.HideBar();     // aberta de fato: fecha pra bandeja
    }

    /// <summary>Balão de notificação da bandeja (usado pelo pomodoro).</summary>
    public void Notify(string title, string msg)
        => _tray?.ShowBalloonTip(4000, title, msg, WinForms.ToolTipIcon.Info);

    /// <summary>Texto do tooltip da bandeja (mostra o pomodoro correndo).</summary>
    public void SetTrayText(string text)
    {
        if (_tray != null)
            _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeySource != null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HOTKEY_TOGGLE);
            UnregisterHotKey(_hotkeySource.Handle, HOTKEY_NOTES);
            _hotkeySource.Dispose();
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
