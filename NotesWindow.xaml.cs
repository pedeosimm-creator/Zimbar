using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Zimbar;

/// <summary>
/// ZimNotes solto (Ctrl+Alt+D): a MESMA biblioteca do painel embutido no
/// Zimbar, agora numa janela própria. Não desenha nada em WPF — hospeda
/// ui/notas.html (notas.css + notas.js, os mesmos cards e chips). É por isso
/// que a janela solta é IGUAL à de dentro, não parecida.
///
/// Clicar numa nota abre a autoadesiva nativa (StickyWindow). A janela é sem
/// moldura: mover/redimensionar voltam pro C# como no BarWindow.
/// </summary>
public partial class NotesWindow : Window
{
    private static NotesWindow? _instance;
    private bool _pronta;

    public static void Open()
    {
        _instance ??= new NotesWindow();
        if (_instance.WindowState == WindowState.Minimized)
            _instance.WindowState = WindowState.Normal;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>As autoadesivas chamam ao salvar/excluir pra a lista atualizar.</summary>
    public static void RefreshIfOpen()
    {
        var win = _instance;
        if (win is null || !win._pronta || win.Web.CoreWebView2 is null) return;
        win.Dispatcher.Invoke(() =>
        {
            try { win.Web.CoreWebView2.PostWebMessageAsString("{\"evento\":\"recarregar\"}"); } catch { }
        });
    }

    private NotesWindow()
    {
        InitializeComponent();
        Cantos.ArredondarQuandoAbrir(this);
        // lembra o tamanho/posição que o Pedro deixou (abre igual toda vez)
        if (Config.NotasWidth is double w && Config.NotasHeight is double h && w > 200 && h > 200)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = w; Height = h;
            if (Config.NotasLeft is double l) Left = l;
            if (Config.NotasTop is double t) Top = t;
        }
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        // identidade própria na barra de tarefas (ícone do ZimNotes, não o do Zimbar)
        IdentidadeJanela.Definir(helper.Handle, "PedroKuster.ZimNotes");
        Closing += (_, _) =>
        {
            var r = RestoreBounds;   // vale mesmo se estiver maximizada
            if (!r.IsEmpty)
            {
                Config.NotasLeft = r.Left; Config.NotasTop = r.Top;
                Config.NotasWidth = r.Width; Config.NotasHeight = r.Height;
                Config.Save();
            }
        };
        Closed += (_, _) => _instance = null;
        _ = IniciarSeguro();
    }

    private static string PastaUi
    {
        get
        {
            string aoLado = Path.Combine(AppContext.BaseDirectory, "ui");
            if (File.Exists(Path.Combine(aoLado, "notas.html"))) return aoLado;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string tenta = Path.Combine(dir.FullName, "ui");
                if (File.Exists(Path.Combine(tenta, "notas.html"))) return tenta;
                dir = dir.Parent;
            }
            return aoLado;
        }
    }

    private async Task IniciarSeguro()
    {
        try { await Iniciar(); }
        catch (Exception ex)
        {
            Log.Erro("subir webview das notas", ex);
            MessageBox.Show("O ZimNotes não conseguiu abrir.\n\n" + ex.Message,
                            "ZimNotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task Iniciar()
    {
        string perfil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zimbar", "WebView2");
        Directory.CreateDirectory(perfil);

        var ambiente = await CoreWebView2Environment.CreateAsync(null, perfil);
        await Web.EnsureCoreWebView2Async(ambiente);

        var nucleo = Web.CoreWebView2;
        var cfg = nucleo.Settings;
        cfg.AreDefaultContextMenusEnabled = false;
        cfg.IsStatusBarEnabled = false;
        cfg.IsZoomControlEnabled = false;
#if DEBUG
        cfg.AreDevToolsEnabled = true;
#else
        cfg.AreDevToolsEnabled = false;
#endif
        nucleo.SetVirtualHostNameToFolderMapping(
            "zimbar.local", PastaUi, CoreWebView2HostResourceAccessKind.Allow);
        nucleo.WebMessageReceived += (_, e) => Receber(e.TryGetWebMessageAsString());
        nucleo.NewWindowRequested += (_, e) => { e.Handled = true; Ponte.AbrirNoNavegador(e.Uri); };

        nucleo.Navigate("https://zimbar.local/notas.html");
        _pronta = true;
    }

    private async void Receber(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return;
        JsonObject? m;
        try { m = JsonNode.Parse(bruto) as JsonObject; }
        catch { return; }
        if (m is null) return;

        switch (m["acao"]?.GetValue<string>())
        {
            case "arrastar":
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                Empurrar(HTCAPTION);
                break;
            case "redimensionar":
                Empurrar(m["borda"]?.GetValue<string>() switch
                {
                    "n" => HTTOP, "s" => HTBOTTOM, "w" => HTLEFT, "e" => HTRIGHT,
                    "nw" => HTTOPLEFT, "ne" => HTTOPRIGHT, "sw" => HTBOTTOMLEFT, "se" => HTBOTTOMRIGHT,
                    _ => HTCAPTION
                });
                break;
            case "maximizar":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "fechar":
                Close();
                break;
            case "abrirNota":
                StickyWindow.OpenNote(
                    m["id"]?.GetValue<string>() ?? "",
                    m["titulo"]?.GetValue<string>() ?? "",
                    m["corpo"]?.GetValue<string>() ?? "",
                    m["cor"]?.GetValue<string>() ?? "");
                break;
            case "novaNota":
                await NovaNota();
                break;
            case "abrirLink":
                Ponte.AbrirNoNavegador(m["url"]?.GetValue<string>() ?? "");
                break;
        }
    }

    /// <summary>Cria a nota no banco e abre a autoadesiva; a lista recarrega.</summary>
    private async Task NovaNota()
    {
        try
        {
            string id = Supa.NewId();
            await Supa.Insert("notas", new JsonObject
            {
                ["id"] = id,
                ["titulo"] = "",
                ["corpo"] = "",
                ["data_nota"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["cor"] = ""
            });
            StickyWindow.OpenNote(id, "", "", "");
            RefreshIfOpen();
        }
        catch (Exception ex) { Log.Erro("nova nota", ex); }
    }

    // O clique nasce dentro do WebView2 (outra janela): devolve pro Windows
    // como se fosse na moldura.
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
                      HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15,
                      HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);

    private void Empurrar(int parte)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(h, WM_NCLBUTTONDOWN, new IntPtr(parte), IntPtr.Zero);
    }
}
