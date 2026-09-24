namespace EyeCtrl.App;

using System.Buffers.Binary;
using System.IO;

/// <summary>
/// Parses just enough RIFF/WAVE to decide whether System.Media.SoundPlayer can play the file.
/// SoundPlayer.Load() does NOT validate a file-path wav — measured 2026-09-24: garbage bytes
/// load "successfully" and the header is first checked inside Play(), which throws on the
/// calling thread. The cue service validates here instead (spec §Mechanism step 4).
/// WAVE_FORMAT_EXTENSIBLE (0xFFFE) is deliberately rejected: SoundPlayer's own Play() path
/// only accepts classic PCM on .NET — a file this parser rejects would throw there.
/// </summary>
internal sealed record Pcm16Header(int SampleRateHz, int ChannelCount, int BitsPerSample, long DataByteCount, long DataOffset);

internal static class WavHeader
{
    /// <summary>A PCM (format tag 1) 16-bit RIFF/WAVE file's header, or null for anything else.</summary>
    internal static Pcm16Header? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> riff = stackalloc byte[12];
            if (fs.Read(riff) != 12
                || !riff[0..4].SequenceEqual("RIFF"u8)
                || !riff[8..12].SequenceEqual("WAVE"u8))
                return null;

            Span<byte> chunkHead = stackalloc byte[8];
            while (fs.Read(chunkHead) == 8)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHead[4..8]);
                if (chunkHead[0..4].SequenceEqual("fmt "u8))
                {
                    if (size < 16) return null;
                    Span<byte> fmt = stackalloc byte[16];
                    if (fs.Read(fmt) != 16) return null;
                    var tag = BinaryPrimitives.ReadInt16LittleEndian(fmt[0..2]);
                    if (tag != 1) return null; // PCM only (see class doc for EXTENSIBLE)
                    var channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                    var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                    var bits = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);
                    if (bits != 16) return null;
                    // WAVEFORMATEX carries extra bytes (cbSize etc.) — skip the REST of the
                    // chunk before hunting 'data', or the hunt starts mid-chunk (G1).
                    fs.Seek((size - 16) + (size & 1), SeekOrigin.Current);
                    var data = FindDataChunk(fs, out var dataOffset);
                    if (data is null) return null;
                    return new Pcm16Header(rate, channels, bits, data.Value, dataOffset);
                }
                fs.Seek(size + (size & 1), SeekOrigin.Current); // chunks are word-aligned
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static long? FindDataChunk(FileStream fs, out long dataOffset)
    {
        Span<byte> chunkHead = stackalloc byte[8];
        while (fs.Read(chunkHead) == 8)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(chunkHead[4..8]);
            if (chunkHead[0..4].SequenceEqual("data"u8))
            {
                dataOffset = fs.Position;
                return size;
            }
            fs.Seek(size + (size & 1), SeekOrigin.Current);
        }
        dataOffset = 0;
        return null;
    }
}
