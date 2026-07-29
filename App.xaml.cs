using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

        Log.Escrever("subindo — args: " + string.Join(" ", e.Args));
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Erro("interface", ev.Exception);
            MessageBox.Show("Deu ruim: " + ev.Exception.Message + "\n\nDetalhes em " + Log.Caminho,
                            "Zimbar", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception x) Log.Erro("fora da interface", x);
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => Log.Erro("tarefa solta", ev.Exception);

        bool querNotas = Array.Exists(e.Args, a => a == "--notas");

        _singleInstance = new Mutex(true, "Zimbar.SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Já tem um Zimbar de pé. Se este atalho pediu o ZimNotes, avisa
            // aquele e sai — é isso que faz o atalho da área de trabalho abrir
            // as notas em vez de simplesmente não fazer nada.
            if (querNotas)
                try { EventWaitHandle.OpenExisting(SinalNotas).Set(); } catch { }
            Shutdown();
            return;
        }
        EscutarPedidoDeNotas();

        // NÃO definir um AppUserModelID explícito: os atalhos (área de trabalho/inicializar/
        // fixado) usam a identidade derivada do caminho do exe. Se o processo usasse uma
        // identidade custom, a janela minimizada não agruparia com o atalho e apareceriam
        // DOIS botões de Zimbar na barra de tarefas.
        Config.Load();
        ThemeManager.Apply(Config.Theme);
        SetupTray();
        SetupHotkey();

        // carimba os atalhos com o AppUserModelID certo (Zimbar / ZimNotes) pra
        // a barra de tarefas dar um botão e um ícone pra cada. Roda no ocioso.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { IdentidadeJanela.EstamparAtalhos(); } catch (Exception ex) { Log.Erro("estampar atalhos", ex); }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Atalho do ZimNotes: abre só a biblioteca de notas, sem a barra.
        if (querNotas)
            NotesWindow.Open();
        // Modo de teste: abre a barra direto na inicialização.
        else if (Array.Exists(e.Args, a => a == "--show"))
            ToggleBar();
        else
            // Subiu pra bandeja: esquenta a interface enquanto ninguém olha.
            Dispatcher.BeginInvoke(new Action(PrepararBarra),
                                   System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    }

    /// <summary>Nome do sinal que o atalho do ZimNotes usa pra falar com a instância viva.</summary>
    private const string SinalNotas = "Zimbar.AbrirNotas";

    /// <summary>
    /// Fica de ouvido num evento nomeado: quem clicar no atalho do ZimNotes
    /// com o Zimbar já aberto acorda a biblioteca em vez de abrir um segundo
    /// processo, que o mutex mataria em seguida.
    /// </summary>
    private void EscutarPedidoDeNotas()
    {
        var sinal = new EventWaitHandle(false, EventResetMode.AutoReset, SinalNotas);
        var t = new Thread(() =>
        {
            while (true)
            {
                sinal.WaitOne();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { NotesWindow.Open(); } catch (Exception ex) { Log.Erro("abrir notas", ex); }
                }));
            }
        })
        { IsBackground = true, Name = "sinal-notas" };
        t.Start();
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
        menu.Items.Add("Pomodoro", null, (_, _) => PomoWindow.Abrir());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => { BarWindow.Saindo = true; Shutdown(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleBar();

        _tray.BalloonTipTitle = "Zimbar ativa";
        _tray.BalloonTipText = "Ctrl+Alt+Z pra abrir o cinto de ferramentas.";
        _tray.ShowBalloonTip(3000);
    }

    /// <summary>
    /// O Z da bandeja — que é também o ícone dos balões de notificação.
    /// Tenta o recurso embutido, depois o próprio executável (que carrega o
    /// mesmo .ico), e só no fim desenha um. O desenho de emergência segue a
    /// marca atual: Z terracota sobre papel, sem placa amarela.
    /// </summary>
    private static Drawing.Icon CreateZIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/assets/Zimbar.ico");
            var info = GetResourceStream(uri);
            if (info?.Stream is System.IO.Stream s)
            {
                Log.Escrever("bandeja: ícone do recurso embutido");
                return new Drawing.Icon(s, new Drawing.Size(32, 32));
            }
        }
        catch (Exception ex) { Log.Escrever("bandeja: recurso falhou — " + ex.Message); }

        // O exe carrega o ApplicationIcon; é a mesma arte, por outro caminho.
        try
        {
            var doExe = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
            if (doExe is not null)
            {
                Log.Escrever("bandeja: ícone tirado do executável");
                return doExe;
            }
        }
        catch (Exception ex) { Log.Escrever("bandeja: exe falhou — " + ex.Message); }

        Log.Escrever("bandeja: caiu no desenho de emergência");
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;
            using var tinta = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xCB, 0x4F, 0x2F));
            using var font = new Drawing.Font("Segoe UI", 26, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            var fmt = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("Z", font, tinta, new Drawing.RectangleF(0, 0, 32, 32), fmt);
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

    /// <summary>
    /// Deixa a barra pronta antes de alguém pedir. O WebView2 leva alguns
    /// segundos pra subir e a interface ainda busca os dados no Supabase —
    /// esse custo é pago aqui, com o app parado na bandeja, e não no
    /// primeiro Ctrl+Alt+Z. EnsureHandle dá o HWND que o WebView2 precisa
    /// sem que a janela apareça.
    /// </summary>
    private void PrepararBarra()
    {
        if (_bar is not null) return;
        _bar = new BarWindow();
        // Se um dia ela fechar de verdade, esquecemos a referência: Show()
        // numa janela já fechada estoura, e o hotkey ficava morto até
        // reiniciar o app.
        _bar.Closed += (_, _) => _bar = null;
        try { new WindowInteropHelper(_bar).EnsureHandle(); }
        catch (Exception ex) { Log.Erro("preparar barra", ex); }
    }

    private void ToggleBar()
    {
        PrepararBarra();
        if (_bar!.IsVisible)
            _bar.CollapseBar();   // aberta: o hotkey esconde (Esc faz o mesmo)
        else
            _bar.ShowBar();
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
