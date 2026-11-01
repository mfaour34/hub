namespace Hub.Core.Audio.Engine;

/// <summary>
/// Which channel an app plays on. Everything is Game unless it's a voice-chat app (Chat), a video player (Media) or
/// a music player (Music); the user's own choices override the lists.
/// </summary>
public static class AppChannels
{
    static readonly HashSet<string> Chat = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "vesktop", "ts3client_win64", "ts3client_win32", "teamspeak", "mumble",
        "skype", "ms-teams", "teams", "zoom", "slack", "signal", "whatsapp", "telegram", "guilded", "element",
    };

    static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv", "mpc-hc64", "mpc-be64", "wmplayer", "microsoft.media.player", "potplayermini64",
    };

    static readonly HashSet<string> Music = new(StringComparer.OrdinalIgnoreCase)
    {
        "spotify", "foobar2000", "aimp", "itunes", "applemusic", "tidal", "deezer", "amazon music", "music.ui",
        "musicbee", "winamp", "qobuz", "soundcloud", "youtube music", "cider",
    };

    public static bool IsMusicPlayer(string processName) => Music.Contains(processName);

    public static AudioChannel For(string processName, IReadOnlyDictionary<string, string> userChoices)
    {
        if (userChoices.TryGetValue(processName.ToLowerInvariant(), out var choice) && Enum.TryParse<AudioChannel>(choice, true, out var ch))
            return ch;
        return Chat.Contains(processName) ? AudioChannel.Chat : Music.Contains(processName) ? AudioChannel.Music
            : Media.Contains(processName) ? AudioChannel.Media : AudioChannel.Game;
    }
}
