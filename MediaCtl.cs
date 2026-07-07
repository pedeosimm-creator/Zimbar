using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Zimbar;

public record NowPlayingInfo(string Title, string Artist, string App, bool Playing);

/// <summary>
/// Player minimalista: lê o que está tocando em QUALQUER app (Spotify, browser,
/// etc.) via GlobalSystemMediaTransportControls do Windows — sem OAuth, sem login.
/// </summary>
public static class MediaCtl
{
    private static async Task<GlobalSystemMediaTransportControlsSession?> Session()
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return mgr.GetCurrentSession();
    }

    public static async Task<NowPlayingInfo?> Get()
    {
        try
        {
            var s = await Session();
            if (s is null) return null;
            var p = await s.TryGetMediaPropertiesAsync();
            if (string.IsNullOrWhiteSpace(p?.Title)) return null;
            bool playing = s.GetPlaybackInfo()?.PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            string app = s.SourceAppUserModelId ?? "";
            if (app.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) app = "Spotify";
            else if (app.Contains('.')) app = app.Split('.')[0];
            return new NowPlayingInfo(p.Title, p.Artist ?? "", app, playing);
        }
        catch { return null; }
    }

    public static async Task Toggle()
    { try { var s = await Session(); if (s is not null) await s.TryTogglePlayPauseAsync(); } catch { } }

    public static async Task Next()
    { try { var s = await Session(); if (s is not null) await s.TrySkipNextAsync(); } catch { } }

    public static async Task Prev()
    { try { var s = await Session(); if (s is not null) await s.TrySkipPreviousAsync(); } catch { } }
}
