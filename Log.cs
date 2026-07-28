using System;
using System.IO;

namespace Zimbar;

/// <summary>
/// Diário de bordo simples. Quando algo quebra sem janela na tela, é aqui
/// que dá pra ver o que foi.
/// </summary>
public static class Log
{
    public static readonly string Caminho = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zimbar", "zimbar.log");

    private static readonly object Trava = new();

    public static void Escrever(string msg)
    {
        try
        {
            lock (Trava)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
                File.AppendAllText(Caminho, $"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
            }
        }
        catch { /* nem o log pode derrubar o app */ }
    }

    public static void Erro(string onde, Exception ex)
        => Escrever($"ERRO em {onde}: {ex.GetType().Name} — {ex.Message}\n{ex.StackTrace}");
}
