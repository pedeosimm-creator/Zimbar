using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Zimbar;

/// <summary>
/// Transcrição (OCR) 100% local usando o motor nativo do Windows
/// (Windows.Media.Ocr) — sem depender de API externa. Preferência por português.
/// </summary>
public static class Ocr
{
    public static bool Disponivel => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    private static OcrEngine? CriarEngine()
    {
        // 1) idiomas do perfil do usuário; 2) pt-BR/pt; 3) qualquer instalado
        var eng = OcrEngine.TryCreateFromUserProfileLanguages();
        if (eng != null) return eng;
        foreach (var tag in new[] { "pt-BR", "pt", "en-US", "en" })
        {
            try { var e = OcrEngine.TryCreateFromLanguage(new Language(tag)); if (e != null) return e; }
            catch { }
        }
        var first = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        return first != null ? OcrEngine.TryCreateFromLanguage(first) : null;
    }

    /// <summary>Lê o texto de uma imagem PNG. Devolve "" se não achar nada / não der.</summary>
    public static async Task<string> LerAsync(byte[] png)
    {
        var engine = CriarEngine();
        if (engine is null) return "";

        var ras = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ras);
        writer.WriteBytes(png);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bmp = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bmp);
        return result.Text?.Trim() ?? "";
    }
}
