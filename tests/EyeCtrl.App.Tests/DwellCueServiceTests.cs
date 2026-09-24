namespace EyeCtrl.App.Tests;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.ViewModels.Messages;
using EyeCtrl.Core.Settings;

public class DwellCueServiceTests
{
    /// <summary>Canonical 44-byte PCM16 wav + a few zero samples — valid for WavHeader.</summary>
    private static string WriteValidWav(string dir)
    {
        var path = Path.Combine(dir, $"ok_{Guid.NewGuid():N}.wav");
        using var fs = File.Create(path);
        fs.Write("RIFF"u8);
        fs.Write(new byte[] { 0x2C, 0, 0, 0 }); // RIFF size = 36 + 8-byte payload
        fs.Write("WAVE"u8);
        fs.Write("fmt "u8); fs.Write(new byte[] { 16, 0, 0, 0 });
        fs.Write(new byte[] { 1, 0 });          // PCM
        fs.Write(new byte[] { 1, 0 });          // mono
        fs.Write(new byte[] { 0x44, 0xAC, 0, 0 }); // 44100
        fs.Write(new byte[] { 0x88, 0x58, 0x01, 0 }); // byte rate
        fs.Write(new byte[] { 2, 0 });          // block align
        fs.Write(new byte[] { 16, 0 });         // bits
        fs.Write("data"u8); fs.Write(new byte[] { 8, 0, 0, 0 });
        fs.Write(new byte[8]);                  // 4 zero samples
        return path;
    }

    /// <summary>ICuePlayer fake — SoundPlayer.Play is NOT virtual, so the seam is the interface (B1).</summary>
    private sealed class RecordingPlayer : ICuePlayer
    {
        public int Plays { get; private set; }
        public bool ThrowOnPlay { get; set; }
        public void Play()
        {
            Plays++;
            if (ThrowOnPlay) throw new InvalidOperationException("simulated device failure");
        }
    }

    [Fact]
    public void Enabled_CompletesActivation_Plays()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var settings = new DwellSettings { CueEnabled = true };
            var player = new RecordingPlayer();
            using var svc = new DwellCueService(new WeakReferenceMessenger(), settings, wav, _ => player);

            svc.Receive(new DwellActivationMessage());

            Check.That(player.Plays).IsEqualTo(1);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void Disabled_PlaysNothing_ButStillCountsHandled()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var settings = new DwellSettings { CueEnabled = false };
            var player = new RecordingPlayer();
            using var svc = new DwellCueService(new WeakReferenceMessenger(), settings, wav, _ => player);

            svc.Receive(new DwellActivationMessage());

            Check.That(player.Plays).IsEqualTo(0);
            Check.That(svc.HandledCount).IsEqualTo(1); // counted BEFORE the gate (B4c)
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void GarbageWav_NeverConstructsAPlayer()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"bad_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(bad, new byte[200]); // measured: SoundPlayer.Load() accepts this
            var factoryCalled = false;

            using var svc = new DwellCueService(new WeakReferenceMessenger(),
                new DwellSettings { CueEnabled = true }, bad,
                _ => { factoryCalled = true; return new RecordingPlayer(); });

            svc.Receive(new DwellActivationMessage());

            Check.That(factoryCalled).IsFalse();
        }
        finally
        {
            File.Delete(bad);
        }
    }

    [Fact]
    public void PlayFailure_Latches_PlaysAtMostOnce()
    {
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var settings = new DwellSettings { CueEnabled = true };
            var player = new RecordingPlayer { ThrowOnPlay = true };
            using var svc = new DwellCueService(new WeakReferenceMessenger(), settings, wav, _ => player);

            Check.ThatCode(() => svc.Receive(new DwellActivationMessage())).DoesNotThrow();
            Check.ThatCode(() => svc.Receive(new DwellActivationMessage())).DoesNotThrow();

            Check.That(player.Plays).IsEqualTo(1); // latched: second Receive never reached Play()
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void Dispose_UnregistersFromTheMessenger()
    {
        var messenger = new WeakReferenceMessenger();
        var seen = 0;
        var probe = new ActionProbe(() => seen++);
        messenger.Register(probe);
        var wav = WriteValidWav(Path.GetTempPath());
        try
        {
            var settings = new DwellSettings { CueEnabled = true };
            var player = new RecordingPlayer();
            var svc = new DwellCueService(messenger, settings, wav, _ => player);

            svc.Dispose();
            messenger.Send(new DwellActivationMessage());

            Check.That(player.Plays).IsEqualTo(0); // unregistered — nothing reaches Receive
            Check.That(seen).IsEqualTo(1);         // the unrelated probe still gets it
        }
        finally
        {
            File.Delete(wav);
        }

        messenger.UnregisterAll(probe);
    }

    // internal (not private): the MVVM source generator emits RegisterAll for IRecipient
    // implementations and cannot reference a private nested type (CS0122).
    internal sealed record ActionProbe(Action OnReceive) : IRecipient<DwellActivationMessage>
    {
        public void Receive(DwellActivationMessage message) => OnReceive();
    }
}
