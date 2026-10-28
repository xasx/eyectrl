namespace EyeCtrl.App;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Status;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Wave B ticket B3: plays a short chime once per real tracker disconnect. Subscribes
/// <see cref="ControlStatusChangedMessage"/> in its ctor UNCONDITIONALLY (like <see cref="DwellCueService"/>),
/// so it MUST be eagerly resolved at startup (AppBootstrapper.ResolveEagerServices): DI singletons are
/// lazy and nothing else depends on it.
/// <para>
/// The edge/gate policy is the headless <see cref="StatusChimeGate"/> in Core; this class is only the
/// player half — read the current setting, hand the status to the gate, play through
/// <see cref="ICuePlayer"/>. The wav header is validated by <see cref="WavHeader"/> before a player is
/// built, and a Play() failure latches the player to null so a broken audio device logs once rather
/// than once per disconnect episode (the DwellCueService contract).
/// </para>
/// </summary>
internal sealed class StatusChimeService : IRecipient<ControlStatusChangedMessage>, IDisposable
{
    private readonly DwellSettings _settings;
    private readonly IMessenger _messenger;
    private readonly StatusChimeGate _gate = new();
    private ICuePlayer? _player;

    /// <summary>Instance-level wiring observation: bumped BEFORE the gates so a disabled service
    /// still proves it is subscribed (the DwellCueService B4c pattern).</summary>
    internal long HandledCount { get; private set; }

    public StatusChimeService(IMessenger messenger, DwellSettings settings, string wavPath,
        Func<string, ICuePlayer?>? playerFactory = null)
    {
        _messenger = messenger;
        _settings = settings;
        _messenger.Register(this); // unconditional: wiring must not depend on the asset
        if (WavHeader.TryRead(wavPath) is null) return;
        var factory = playerFactory ?? (p => File.Exists(p) ? new SoundPlayerCuePlayer(p) : null);
        _player = factory(wavPath);
    }

    public void Receive(ControlStatusChangedMessage message)
    {
        HandledCount++;
        if (!_gate.ShouldChime(
                new ControlStatus(message.State, message.Disconnected),
                _settings.StatusChimeEnabled))
            return;

        if (_player is null) return;
        try
        {
            _player.Play();
        }
        catch (Exception ex)
        {
            _player = null; // latch: one log and one attempt per broken device, not per episode
            Log.Warning(ex, "Status chime sound failed; chime disabled for this session.");
        }
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
