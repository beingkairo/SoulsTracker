using System.IO;
using System.Windows.Media;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

/// <summary>Desktop-only, best-effort local audio playback. It never affects tracking state.</summary>
public interface IDeathSoundPlayer { void Play(DeathSoundConfiguration configuration); }

/// <summary>Small seam so the active WPF player lifetime is verifiable without audio hardware.</summary>
internal interface ILocalDeathSoundMedia
{
    event EventHandler? Ended;
    event EventHandler? Failed;
    void Open(Uri source);
    void SetVolume(double volume);
    void Play();
    void Close();
}

internal interface ILocalDeathSoundMediaFactory { ILocalDeathSoundMedia Create(); }

internal sealed class WpfDeathSoundMediaFactory : ILocalDeathSoundMediaFactory
{
    public ILocalDeathSoundMedia Create() => new WpfDeathSoundMedia();
}

internal sealed class WpfDeathSoundMedia : ILocalDeathSoundMedia
{
    private readonly MediaPlayer player = new();
    public WpfDeathSoundMedia()
    {
        player.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        player.MediaFailed += (_, _) => Failed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Ended;
    public event EventHandler? Failed;
    public void Open(Uri source) => player.Open(source);
    public void SetVolume(double volume) => player.Volume = volume;
    public void Play() => player.Play();
    public void Close() => player.Close();
}

public sealed class WpfDeathSoundPlayer : IDeathSoundPlayer
{
    private readonly object gate = new();
    private readonly ILocalDeathSoundMediaFactory factory;
    private ILocalDeathSoundMedia? activePlayer;

    public WpfDeathSoundPlayer() : this(new WpfDeathSoundMediaFactory()) { }
    internal WpfDeathSoundPlayer(ILocalDeathSoundMediaFactory factory) => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    internal bool IsPlaying { get { lock (gate) return activePlayer is not null; } }

    public void Play(DeathSoundConfiguration configuration)
    {
        if (!configuration.IsEnabled || string.IsNullOrWhiteSpace(configuration.LocalPath) || !File.Exists(configuration.LocalPath)) return;
        ILocalDeathSoundMedia media;
        lock (gate)
        {
            if (activePlayer is not null) return;
            media = factory.Create();
            activePlayer = media;
        }
        EventHandler? cleanup = null;
        cleanup = (_, _) => Cleanup(media, cleanup!);
        try
        {
            media.Ended += cleanup;
            media.Failed += cleanup;
            media.SetVolume(configuration.Volume / 100d);
            media.Open(new Uri(configuration.LocalPath, UriKind.Absolute));
            media.Play();
        }
        catch { Cleanup(media, cleanup); }
    }

    private void Cleanup(ILocalDeathSoundMedia media, EventHandler cleanup)
    {
        media.Ended -= cleanup;
        media.Failed -= cleanup;
        try { media.Close(); }
        catch { }
        lock (gate)
        {
            if (ReferenceEquals(activePlayer, media)) activePlayer = null;
        }
    }
}
