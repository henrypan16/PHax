using PHax.Core.Interfaces;
using PHax.Core.Models;

namespace PHax.Imaging.Encoding;

/// <summary>
/// T.4 (MH, MR) and T.6 (MMR) encoder.
/// Fax-line streams (<see cref="EncodeT4"/>) use an EOL before each line, fill bits to satisfy the
/// negotiated minimum scan-line time, byte-aligned EOLs and a closing RTC.
/// </summary>
public sealed class T4Encoder : IFaxImageEncoder
{
    public byte[] EncodeT4(FaxBitmap page, FaxCompression compression, int minBitsPerLine, bool lsbFirst)
    {
        int width = page.Width;
        int k = compression == FaxCompression.MR ? (page.Resolution == FaxResolution.Fine ? 4 : 2) : 1;
        var w = new BitWriter(page.Height * 64 + 64);
        var reference = new byte[width];
        var current = new byte[width];

        for (int y = 0; y < page.Height; y++)
        {
            Unpack(page, y, current);
            bool oneD = compression == FaxCompression.MH || y % k == 0;

            AlignForEol(w, compression);
            long lineStart = w.BitPosition;
            w.Write(T4Codes.EolBits, T4Codes.EolLength);
            if (compression == FaxCompression.MR) w.Write(oneD ? 1 : 0, 1);

            if (oneD) EncodeLine1D(w, current);
            else EncodeLine2D(w, reference, current);

            long used = w.BitPosition - lineStart;
            if (used < minBitsPerLine) w.WriteZeros((int)(minBitsPerLine - used));

            (reference, current) = (current, reference);
        }

        // RTC: six consecutive EOLs (each followed by a 1 tag bit in MR).
        AlignForEol(w, compression);
        for (int i = 0; i < 6; i++)
        {
            w.Write(T4Codes.EolBits, T4Codes.EolLength);
            if (compression == FaxCompression.MR) w.Write(1, 1);
        }
        return w.ToArray(lsbFirst);
    }

    /// <summary>T.6 (Group 4) encoding without EOLs, terminated by EOFB. Bits are MSB-first, as stored in TIFF and PDF.</summary>
    public byte[] EncodeT6(FaxBitmap page)
    {
        var w = new BitWriter(page.Height * 32 + 64);
        var reference = new byte[page.Width];
        var current = new byte[page.Width];
        for (int y = 0; y < page.Height; y++)
        {
            Unpack(page, y, current);
            EncodeLine2D(w, reference, current);
            (reference, current) = (current, reference);
        }
        w.Write(T4Codes.EolBits, T4Codes.EolLength);
        w.Write(T4Codes.EolBits, T4Codes.EolLength);
        return w.ToArray();
    }

    /// <summary>Pads so that the EOL (and MR tag bit, if any) ends on a byte boundary.</summary>
    private static void AlignForEol(BitWriter w, FaxCompression compression)
    {
        int eolBits = T4Codes.EolLength + (compression == FaxCompression.MR ? 1 : 0);
        int rem = (int)((w.BitPosition + eolBits) % 8);
        if (rem != 0) w.WriteZeros(8 - rem);
    }

    internal static void Unpack(FaxBitmap page, int y, byte[] pixels)
    {
        var row = page.Row(y);
        for (int x = 0; x < page.Width; x++)
            pixels[x] = (byte)((row[x >> 3] >> (7 - (x & 7))) & 1);
    }

    internal static void EncodeLine1D(BitWriter w, byte[] line)
    {
        int width = line.Length;
        int pos = 0;
        byte color = 0;
        while (pos < width)
        {
            int end = pos;
            while (end < width && line[end] == color) end++;
            WriteRun(w, color == 1, end - pos);
            pos = end;
            color ^= 1;
        }
        // A line that ends on a white run is complete; a line ending black is too.
        // An entirely empty line cannot occur because width > 0.
    }

    internal static void WriteRun(BitWriter w, bool black, int run)
    {
        while (run >= 2560 + 64)
        {
            w.Write(T4Codes.Makeup(black, 2560));
            run -= 2560;
        }
        if (run >= 64)
        {
            int makeup = run / 64 * 64;
            w.Write(T4Codes.Makeup(black, makeup));
            run -= makeup;
        }
        w.Write(T4Codes.Terminating(black, run));
    }

    /// <summary>Two-dimensional coding of one line against its reference line (T.4 section 4.2).</summary>
    internal static void EncodeLine2D(BitWriter w, byte[] reference, byte[] coding)
    {
        int width = coding.Length;
        int a0 = -1;
        byte color = 0;
        while (a0 < width)
        {
            int a1 = NextColorChange(coding, a0, color);
            int b1 = FindB1(reference, a0, color);
            int b2 = NextChange(reference, b1);

            if (b2 < a1)
            {
                w.Write(T4Codes.Pass);
                a0 = b2;
            }
            else if (Math.Abs(a1 - b1) <= 3)
            {
                w.Write(T4Codes.Vertical[a1 - b1 + 3]);
                a0 = a1;
                color ^= 1;
            }
            else
            {
                int a2 = a1 < width ? NextColorChange(coding, a1, (byte)(color ^ 1)) : width;
                int start = Math.Max(a0, 0);
                w.Write(T4Codes.Horizontal);
                WriteRun(w, color == 1, a1 - start);
                WriteRun(w, color == 0, a2 - a1);
                a0 = a2;
            }
        }
    }

    /// <summary>First position after <paramref name="a0"/> whose colour differs from <paramref name="color"/>, or width.</summary>
    internal static int NextColorChange(byte[] line, int a0, byte color)
    {
        int p = a0 < 0 ? 0 : a0 + 1;
        while (p < line.Length && line[p] == color) p++;
        return p;
    }

    /// <summary>b1: first changing element on the reference line right of a0 with colour opposite to a0's colour.</summary>
    internal static int FindB1(byte[] reference, int a0, byte color)
    {
        int p = a0 < 0 ? 0 : a0 + 1;
        for (; p < reference.Length; p++)
        {
            byte prev = p == 0 ? (byte)0 : reference[p - 1];
            if (reference[p] != color && prev == color) return p;
        }
        return reference.Length;
    }

    /// <summary>Next changing element strictly after <paramref name="b1"/>.</summary>
    internal static int NextChange(byte[] reference, int b1)
    {
        if (b1 >= reference.Length) return reference.Length;
        byte c = reference[b1];
        int p = b1 + 1;
        while (p < reference.Length && reference[p] == c) p++;
        return p;
    }
}
