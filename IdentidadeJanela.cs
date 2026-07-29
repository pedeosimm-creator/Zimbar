using System;
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

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore pps);

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
