namespace EyeCtrl.App;

using System.Runtime.InteropServices;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// Play-through seam for the key click (the ICuePlayer pattern from DwellCueService): tests
/// substitute fakes here, production is <see cref="WinmmClickPlayer"/>. The bool is the point
/// of the seam — winmm's own verdict, which System.Media.SoundPlayer discarded (F5).
/// <para>
/// What the verdict means under SND_ASYNC (measured, review 01 F5): winmm answers BEFORE it
/// opens the file, so true = the request was QUEUED — a missing wav still returns true and the
/// failure happens invisibly on winmm's worker. false = a synchronous refusal (no wave device,
/// invalid call). Neither word means the click was heard; the hide line's played=N failed=0
/// proves only that every request reached winmm.
/// </para>
/// </summary>
internal interface IClickSoundPlayer
{
    /// <summary>true = playback request queued by winmm; false = synchronous refusal (device,
    /// endpoint or audio session) — a silent failure the hide line must now count.</summary>
    bool Play();
}

/// <summary>
/// winmm <c>PlaySound</c> on a file path: the same underlying call SoundPlayer's Play() makes
/// for a path (PlaySoundW, SND_FILENAME|SND_ASYNC|SND_NODEFAULT) with two F5 differences — the
/// wav is validated once by <see cref="WavHeader"/> at construction instead of re-walking the
/// RIFF on every key (SoundPlayer's per-Play ValidateSoundFile), and the return value is
/// surfaced to the caller instead of ignored. SND_NODEFAULT keeps a refused play from falling
/// back to the system beep, so "nothing played" stays observable as a false return.
/// </summary>
internal sealed class WinmmClickPlayer(string wavPath) : IClickSoundPlayer
{
    private const int SndAsync = 0x0001;
    private const int SndFilename = 0x0002;
    private const int SndNodefault = 0x0010;

    public bool Play() => PlaySoundW(wavPath, IntPtr.Zero, SndAsync | SndFilename | SndNodefault);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(string pszSound, IntPtr hmod, int fdwSound);
}

public class KeyboardSoundService
{
    private readonly KeyboardSettings _settings;

    // The wav is validated ONCE here (the DwellCueService pattern), not per play: a missing or
    // non-PCM-16 file leaves the player null and every click "skipped" — silently as before,
    // but now the hide line counts it (F5).
    private IClickSoundPlayer? _player;

    // Per-show counters behind the hide line (F5). Only the UI thread reaches PlayKeyClick /
    // OnKeyboardShown / OnKeyboardHidden (gaze is dispatcher-marshaled), so plain ints suffice.
    private int _requested, _played, _failed, _skipped;
    private bool _failureLoggedThisShow;

    /// <summary>Diagnostics lines land here in tests; null (production) → Serilog Information.</summary>
    internal Action<string>? DiagnosticsLineSink;

    /// <summary>The production constructor: the wav is validated once and played through winmm.</summary>
    public KeyboardSoundService(KeyboardSettings settings, string wavPath)
        : this(settings, wavPath, null)
    {
    }

    /// <summary>Test seam (B1's CS0051 rule: the factory stays off the public ctor's signature).</summary>
    internal KeyboardSoundService(KeyboardSettings settings, string wavPath,
        Func<string, IClickSoundPlayer?>? playerFactory)
    {
        _settings = settings;
        _player = (playerFactory ?? DefaultPlayerFactory)(wavPath);
    }

    /// <summary>The production player factory: header-validated once, then the raw winmm call.</summary>
    internal static IClickSoundPlayer? DefaultPlayerFactory(string path) =>
        WavHeader.TryRead(path) is null ? null : new WinmmClickPlayer(path);

    /// <summary>The hide line's exact format — pinned by a test, one line per real keyboard hide.</summary>
    internal static string FormatHideLine(int requested, int played, int failed, int skipped) =>
        $"Keyboard click: requested={requested} played={played} failed={failed} skipped={skipped}";

    public virtual void PlayKeyClick()
    {
        _requested++;
        if (!_settings.ClickSoundEnabled || _player is null)
        {
            _skipped++;
            return;
        }
        try
        {
            if (_player.Play())
            {
                _played++;
                return;
            }
            // winmm's false is the signal SoundPlayer threw away (F5): count it, log the FIRST
            // failure of the show, never latch — the sound "came back" for the owner, so the
            // next key must attempt again.
            _failed++;
            if (!_failureLoggedThisShow)
            {
                _failureLoggedThisShow = true;
                LogLine("Key click: PlaySound returned false (first failure this show; later ones count only).");
            }
        }
        catch (Exception ex)
        {
            _failed++;
            _player = null; // latch: an unexpected managed failure must cost one log line, not every key
            Log.Warning(ex, "Key click sound failed; click sound disabled for this session.");
        }
        finally
        {
            // Once per app lifetime (statically guarded — the service itself is recreated by
            // every settings apply): the first click snapshots the audio session state and
            // attaches the change listeners. Every later call is one Interlocked read — never
            // COM on the typing path.
            AudioSessionDiagnostics.OnClickAttempted();
        }
    }

    /// <summary>A REAL keyboard show (KeyboardCoordinator decides, E1's shown/hidden discipline):
    /// a new counter window opens for the hide line this show will produce.</summary>
    public void OnKeyboardShown()
    {
        _requested = _played = _failed = _skipped = 0;
        _failureLoggedThisShow = false;
        AudioSessionDiagnostics.OnKeyboardTransition();
    }

    /// <summary>A REAL keyboard hide: exactly one Information line for the show that just ended.</summary>
    public void OnKeyboardHidden()
    {
        LogLine(FormatHideLine(_requested, _played, _failed, _skipped));
        AudioSessionDiagnostics.OnKeyboardTransition();
    }

    private void LogLine(string line)
    {
        var sink = DiagnosticsLineSink;
        if (sink is not null) sink(line);
        else Log.Information("{Line}", line);
    }
}
