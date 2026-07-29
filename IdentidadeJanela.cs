using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zimbar;

/// <summary>
/// Dá a uma janela uma identidade PRÓPRIA na barra de tarefas (AppUserModelID
/// por janela). Sem isso, todas as janelas do mesmo processo se juntam num
/// botão só, e o ícone de uma "vence" o da outra — era por isso que abrir o
/// Zimbar mostrava o ícone do ZimNotes na barra.
///
/// Só a janela do ZimNotes recebe id próprio; a BarWindow fica na identidade
/// padrão (derivada do caminho do exe), a mesma do atalho fixado — então o
/// Zimbar continua com um botão só, sem risco de duplicar.
/// </summary>
internal static class IdentidadeJanela
{
    public static void Definir(IntPtr hwnd, string appId)
    {
        if (hwnd == IntPtr.Zero) return;
        var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"); // IID_IPropertyStore
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return;
        try
        {
            var chave = new PROPERTYKEY
            {
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), // PKEY_AppUserModel_ID
                pid = 5
            };
            var valor = new PROPVARIANT { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                store.SetValue(ref chave, ref valor);
                store.Commit();
            }
            finally { if (valor.p != IntPtr.Zero) Marshal.FreeCoTaskMem(valor.p); }
        }
        catch (Exception ex) { Log.Escrever("identidade da janela: " + ex.Message); }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>
    /// Carimba os atalhos do Zimbar e do ZimNotes com o mesmo AppUserModelID
    /// que as janelas usam. Sem isso, a janela (com id próprio) não se junta
    /// ao atalho fixado — daria DOIS botões na barra. É idempotente e barato,
    /// então roda em toda inicialização (aguenta reinstalar por cima).
    /// </summary>
    public static void EstamparAtalhos()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string menu = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs");
        string startup = Path.Combine(menu, "Startup");
        string taskbar = Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        foreach (var p in new[] {
            Path.Combine(menu, "Zimbar.lnk"),
            Path.Combine(startup, "Zimbar.lnk"),
            Path.Combine(taskbar, "Zimbar.lnk"),
        }) CarimbarAtalho(p, "PedroKuster.Zimbar");

        foreach (var p in new[] {
            Path.Combine(desktop, "ZimNotes.lnk"),
            Path.Combine(menu, "ZimNotes.lnk"),
        }) CarimbarAtalho(p, "PedroKuster.ZimNotes");
    }

    private static void CarimbarAtalho(string caminho, string appId)
    {
        if (!File.Exists(caminho)) return;
        try
        {
            var link = (IPersistFile)new CShellLink();
            link.Load(caminho, 2 /* STGM_READWRITE */);
            var store = (IPropertyStore)link;
            var chave = new PROPERTYKEY
            {
                fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
                pid = 5
            };
            // já está certo? não mexe (não reescreve o atalho à toa)
            store.GetValue(ref chave, out var atual);
            string jaTem = atual.vt == 31 && atual.p != IntPtr.Zero ? Marshal.PtrToStringUni(atual.p) ?? "" : "";
            PropVariantClear(ref atual);
            if (jaTem == appId) { Marshal.ReleaseComObject(store); Marshal.ReleaseComObject(link); return; }

            var valor = new PROPVARIANT { vt = 31, p = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref chave, ref valor); store.Commit(); }
            finally { if (valor.p != IntPtr.Zero) Marshal.FreeCoTaskMem(valor.p); }
            link.Save(caminho, true);
            Marshal.ReleaseComObject(store);
            Marshal.ReleaseComObject(link);
        }
        catch (Exception ex) { Log.Escrever($"carimbar {Path.GetFileName(caminho)}: {ex.Message}"); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore pps);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint c);
        int GetAt(uint i, out PROPERTYKEY k);
        int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        int Commit();
    }
}
