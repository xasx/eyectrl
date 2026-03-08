namespace EyeCtrl.App;

using System.IO;
using System.Media;
using EyeCtrl.Core.Settings;

public class KeyboardSoundService(KeyboardSettings settings, string wavPath)
{
    private readonly SoundPlayer? _player = File.Exists(wavPath) ? new SoundPlayer(wavPath) : null;

    public void PlayKeyClick()
    {
        if (settings.ClickSoundEnabled) _player?.Play();
    }
}
