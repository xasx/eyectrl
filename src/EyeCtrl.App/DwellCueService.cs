namespace EyeCtrl.App;

using System.IO;
using System.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>Play-through seam for the cue player. SoundPlayer.Play is not virtual (measured
/// .NET 10.0.12), so tests substitute this interface instead of subclassing SoundPlayer (B1).</summary>
internal interface ICuePlayer
{
    void Play();
}

/// <summary>SoundPlayer-backed ICuePlayer — the production implementation.</summary>
internal sealed class SoundPlayerCuePlayer(string wavPath) : ICuePlayer
{
    private readonly SoundPlayer _player = new(wavPath);
    public void Play() => _player.Play();
}

/// <summary>
/// Plays the dwell-completion cue (spec 2026-09-23). Subscribes DwellActivationMessage in its
/// ctor UNCONDITIONALLY — subscription must not depend on the asset being valid (a null player
/// is already silent) — so it MUST be eagerly resolved at startup (App.xaml.cs via
/// AppBootstrapper.ResolveEagerServices): DI singletons are lazy and nothing else depends on it.
/// The wav header is validated by WavHeader, not SoundPlayer.Load() (which does not validate
/// file-path wavs — measured); a Play() failure latches the player to null so a
/// deleted-after-startup file logs once, not once per dwell. IDisposable: the container disposes
/// singletons, and disposal unregisters from the messenger (spec review 2 #6 teardown isolation).
/// Internal because the ctor's playerFactory parameter exposes the internal ICuePlayer seam
/// (B1) — a public ctor signature over it is CS0051; the test assembly sees it via
/// InternalsVisibleTo, and Task 6's eager DI resolution is same-assembly.
/// </summary>
internal sealed class DwellCueService : IRecipient<DwellActivationMessage>, IDisposable
{
    private readonly DwellSettings _settings;
    private readonly IMessenger _messenger;
    private ICuePlayer? _player;

    /// <summary>Instance-level wiring observation (B4c): static counters race with parallel
    /// test classes; this is read from the resolved instance, bumped BEFORE the enabled gate
    /// so a disabled service still proves it is wired.</summary>
    internal long HandledCount { get; private set; }

    public DwellCueService(IMessenger messenger, DwellSettings settings, string wavPath,
        Func<string, ICuePlayer?>? playerFactory = null)
    {
        _messenger = messenger;
        _settings = settings;
        _messenger.Register(this); // unconditional (B4d): wiring must not depend on the asset
        if (WavHeader.TryRead(wavPath) is null) return;
        var factory = playerFactory ?? (p => File.Exists(p) ? new SoundPlayerCuePlayer(p) : null);
        _player = factory(wavPath);
    }

    public void Receive(DwellActivationMessage message)
    {
        HandledCount++;
        if (!_settings.CueEnabled || _player is null) return;
        try
        {
            _player.Play();
        }
        catch (Exception ex)
        {
            _player = null; // latch (spec §Error handling)
            Log.Warning(ex, "Dwell cue sound failed; cue disabled for this session.");
        }
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
