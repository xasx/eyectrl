#!/usr/bin/env python3
"""Generates src/EyeCtrl.App/Assets/status-chime.wav - Wave B ticket B3's tracker-disconnect chime.
Parameters are PINNED here and asserted by StatusChimeAssetTests; regenerate only deliberately,
together with that test.
16-bit PCM, 44.1 kHz, mono, two descending notes (784 Hz then 523 Hz), 100 ms each with a 10 ms
gap (210 ms total), 4 ms linear attack and ~45 ms exponential decay per note, peak 0.12 (-18.4
dBFS). Two notes make it clearly distinct from the 50 ms single-tone dwell cue and the key click;
descending reads as "lost", not "done".
"""
import math
import struct
import wave

SR = 44_100
NOTES = (784.0, 523.25)
NOTE_DUR = 0.100
GAP = 0.010
ATTACK = 0.004
TAU = 0.045
PEAK = 0.12  # -18.4 dBFS


def note(freq: float) -> list[int]:
    n = int(SR * NOTE_DUR)
    frames = []
    for i in range(n):
        t = i / SR
        attack = min(1.0, t / ATTACK)
        env = attack * math.exp(-t / TAU)
        frames.append(int(round(PEAK * env * math.sin(2 * math.pi * freq * t) * 32_767)))
    return frames


def main() -> None:
    samples = note(NOTES[0]) + [0] * int(SR * GAP) + note(NOTES[1])
    frames = b"".join(struct.pack("<h", s) for s in samples)
    with wave.open("src/EyeCtrl.App/Assets/status-chime.wav", "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(frames)
    print(f"wrote src/EyeCtrl.App/Assets/status-chime.wav ({len(frames)} data bytes)")


if __name__ == "__main__":
    main()
