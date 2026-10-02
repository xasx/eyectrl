namespace EyeCtrl.IconBuilder;

using System.IO;

internal static class IcoWriter
{
    /// <summary>
    /// Packs the ascending frames into an ICO: BMP payloads (32bpp BI_RGB, biHeight = 2·h,
    /// rows bottom-up, AND mask bottom-up padded to 4 bytes with bits set where α == 0)
    /// for sizes below 256, PNG for the 256 entry (directory width/height = 0).
    /// All directory entries: planes = 1, bit count = 32, colour count = 0.
    /// </summary>
    public static byte[] Build(IReadOnlyList<FramePlan> plans)
    {
        var payloads = new List<byte[]>();
        foreach (var plan in plans)
        {
            var bgra = FrameRenderer.RenderBgra(plan);
            payloads.Add(plan.Size == 256
                ? FrameRenderer.EncodePng(plan.Size, bgra)
                : EncodeBmp(plan.Size, bgra));
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((short)0);                       // reserved
        bw.Write((short)1);                       // type: icon
        bw.Write((short)payloads.Count);

        int offset = 6 + 16 * payloads.Count;
        for (int i = 0; i < plans.Count; i++)
        {
            int size = plans[i].Size;
            byte dimension = size == 256 ? (byte)0 : (byte)size;
            bw.Write(dimension);                  // width
            bw.Write(dimension);                  // height
            bw.Write((byte)0);                    // colour count
            bw.Write((byte)0);                    // reserved
            bw.Write((short)1);                   // planes
            bw.Write((short)32);                  // bit count
            bw.Write(payloads[i].Length);
            bw.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads)
            bw.Write(payload);

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodeBmp(int size, byte[] bgraTopDown)
    {
        int andStride = (size + 31) / 32 * 4;     // 1bpp rows padded to 4 bytes
        int andSize = andStride * size;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(40);                             // biSize
        bw.Write(size);                           // biWidth
        bw.Write(size * 2);                       // biHeight = XOR + AND
        bw.Write((short)1);                       // biPlanes
        bw.Write((short)32);                      // biBitCount
        bw.Write(0);                              // biCompression = BI_RGB
        bw.Write(size * 4 * size + andSize);      // biSizeImage
        bw.Write(0);                              // biXPelsPerMeter
        bw.Write(0);                              // biYPelsPerMeter
        bw.Write(0);                              // biClrUsed
        bw.Write(0);                              // biClrImportant

        for (int row = size - 1; row >= 0; row--) // XOR rows, bottom-up
            bw.Write(bgraTopDown.AsSpan(row * size * 4, size * 4).ToArray());

        for (int row = size - 1; row >= 0; row--) // AND rows, bottom-up, MSB-first
        {
            var rowBytes = new byte[andStride];
            for (int x = 0; x < size; x++)
                if (bgraTopDown[(row * size + x) * 4 + 3] == 0)
                    rowBytes[x / 8] |= (byte)(0x80 >> (x % 8));
            bw.Write(rowBytes);
        }

        bw.Flush();
        return ms.ToArray();
    }
}
