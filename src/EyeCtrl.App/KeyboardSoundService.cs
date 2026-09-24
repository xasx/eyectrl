namespace EyeCtrl.App;

using System.IO;
using System.Media;
using EyeCtrl.Core.Settings;
using Serilog;

public class KeyboardSoundService(KeyboardSettings settings, string wavPath)
{
    private SoundPlayer? _player = File.Exists(wavPath) ? new SoundPlayer(wavPath) : null;

    public virtual void PlayKeyClick()
    {
        if (!settings.ClickSoundEnabled || _player is null) return;
        try
        {
            _player.Play();
        }
        catch (Exception ex)
        {
            _player = null; // latch: SoundPlayer validates the wav only in Play() — a corrupt or
                            // deleted-after-startup click.wav must cost one log line, not every key
            Log.Warning(ex, "Key click sound failed; click sound disabled for this session.");
        }
    }
}
