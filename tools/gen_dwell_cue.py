#!/usr/bin/env python3
"""Generates src/EyeCtrl.App/Assets/dwell-cue.wav — the dwell-completion cue (spec 2026-09-23
§Asset, ledger D6). Parameters are PINNED here and asserted by DwellCueAssetTests; regenerate
only deliberately, together with that test.
16-bit PCM, 44.1 kHz, mono, 50 ms, 880 Hz sine, 2.5 ms linear attack, ~12 ms exponential decay,
peak 0.1 (-20 dBFS). The attack is what makes it read as a soft tone rather than a tick (plan G7).
"""
import math
import struct
import wave

SR = 44_100
DUR = 0.050
FREQ = 880.0
ATTACK = 0.0025
TAU = 0.012
PEAK = 0.10  # -20 dBFS

def main() -> None:
    n = int(SR * DUR)
    frames = bytearray()
    for i in range(n):
        t = i / SR
        attack = min(1.0, t / ATTACK) if ATTACK > 0 else 1.0
        env = attack * math.exp(-t / TAU)
        sample = PEAK * env * math.sin(2 * math.pi * FREQ * t)
        frames += struct.pack("<h", int(round(sample * 32_767)))
    with wave.open("src/EyeCtrl.App/Assets/dwell-cue.wav", "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(bytes(frames))
    print(f"wrote src/EyeCtrl.App/Assets/dwell-cue.wav ({len(frames)} data bytes)")

if __name__ == "__main__":
    main()
