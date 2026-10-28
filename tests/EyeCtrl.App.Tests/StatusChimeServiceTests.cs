namespace EyeCtrl.App.Tests;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Wave B ticket B3's runtime half: the chime service subscribes <see cref="ControlStatusChangedMessage"/>
/// in its ctor, asks the headless <c>StatusChimeGate</c> whether this status is a new disconnect
/// episode, and plays the bundled status chime through the <see cref="ICuePlayer"/> seam. These tests
/// use the seam (never a real audio device) and a fresh messenger, so nothing touches the process
/// singleton or the sound card.
/// </summary>
public class StatusChimeServiceTests
{
    /// <summary>Canonical 44-byte PCM16 wav + a few zero samples - valid for WavHeader.</summary>
    private static string WriteValidWav(string dir)
    {
        var path = Path.Combine(dir, $"chime_{Guid.NewGuid():N}.wav");
        using var fs = File.Create(path);
        fs.Write("RIFF"u8);
        fs.Write(new byte[] { 0x2C, 0, 0, 0 });
        fs.Write("WAVE"u8);
        fs.Write("fmt "u8); fs.Write(new byte[] { 16, 0, 0, 0 });
        fs.Write(new byte[] { 1, 0 });          // PCM
        fs.Write(new byte[] { 1, 0 });          // mono
        fs.Write(new byte[] { 0x44, 0xAC, 0, 0 }); // 44100
        fs.Write(new byte[] { 0x88, 0x58, 0x01, 0 }); // byte rate
        fs.Write(new byte[] { 2, 0 });          // block align
        fs.Write(new byte[] { 16, 0 });         // bits
        fs.Write("data"u8); fs.Write(new byte[] { 8, 0, 0, 0 });
        fs.Write(new byte[8]);
        return path;
    }

    private sealed class RecordingPlayer : ICuePlayer
    {
        public int Attempts { get; private set; }
        public bool ThrowOnPlay { get; set; }
        public void Play()
        {
            Attempts++;
            if (ThrowOnPlay) throw new InvalidOperationException("simulated device failure");
        }
    }

    private static ControlStatusChangedMessage Status(ControlState state, bool disconnected) =>
        new(state, disconnected);

    private static StatusChimeService NewService(
        DwellSettings settings, ICuePlayer player, string wavPath) =>
        new(new WeakReferenceMessenger(), settings, wavPath, _ => player);

    [Fact]
    public void Enabled_RisingEdgeOfDisconnected_PlaysExactlyOncePerEpisode()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, wav);

            svc.Receive(Status(ControlState.Normal, false));
            svc.Receive(Status(ControlState.TrackingLost, true));
            svc.Receive(Status(ControlState.TrackingLost, true));
            svc.Receive(Status(ControlState.Normal, true));

            Check.That(player.Attempts).IsEqualTo(1);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void AReconnectThenANewEpisode_PlaysAgain()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, wav);

            svc.Receive(Status(ControlState.TrackingLost, true));
            svc.Receive(Status(ControlState.Normal, false));
            svc.Receive(Status(ControlState.TrackingLost, true));

            Check.That(player.Attempts).IsEqualTo(2);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void SettingOff_PlaysNothing_ButStillCountsHandled()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = false }, player, wav);

            svc.Receive(Status(ControlState.TrackingLost, true));

            Check.That(player.Attempts).IsEqualTo(0);
            Check.That(svc.HandledCount).IsEqualTo(1);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void NoTrackerAndTrackingLostAlone_PlayNothing()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, wav);

            svc.Receive(Status(ControlState.NoTracker, true));
            svc.Receive(Status(ControlState.NoTracker, false));
            svc.Receive(Status(ControlState.TrackingLost, false));
            svc.Receive(Status(ControlState.Paused, false));

            Check.That(player.Attempts).IsEqualTo(0);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void PausedWithADisconnect_Plays()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, wav);

            svc.Receive(Status(ControlState.Paused, false));
            svc.Receive(Status(ControlState.Paused, true));

            Check.That(player.Attempts).IsEqualTo(1);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void APlayFailure_IsSwallowed_AndLatchesThePlayerSilent()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var player = new RecordingPlayer { ThrowOnPlay = true };
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, wav);

            svc.Receive(Status(ControlState.TrackingLost, true));
            svc.Receive(Status(ControlState.Normal, false));
            svc.Receive(Status(ControlState.TrackingLost, true));

            // One attempt, one log, latched: a broken device is not retried once per episode.
            Check.That(player.Attempts).IsEqualTo(1);
        }
        finally { File.Delete(wav); }
    }

    [Fact]
    public void AnInvalidWav_BuildsNoPlayer_AndNeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.wav");
        File.WriteAllText(path, "not a wav");
        try
        {
            var player = new RecordingPlayer();
            using var svc = NewService(new DwellSettings { StatusChimeEnabled = true }, player, path);

            svc.Receive(Status(ControlState.TrackingLost, true));

            Check.That(player.Attempts).IsEqualTo(0);
            Check.That(svc.HandledCount).IsEqualTo(1);
        }
        finally { File.Delete(path); }
    }
}
