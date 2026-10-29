using PHax.Core.Interfaces;
using PHax.Core.Models;
using PHax.Imaging.Encoding;

namespace PHax.Imaging.Decoding;

public sealed class FaxDecodeException(string message) : Exception(message);

/// <summary>
/// T.4 (MH/MR) decoder for fax-line streams and T.6 (MMR) decoder for stored TIFF/PDF data.
/// Line errors in T.4 streams are concealed by repeating the previous good line and counted, so the
/// receiver can decide between MCF and RTN.
/// </summary>
public sealed class T4Decoder : IFaxImageDecoder
{
    private sealed class Node
    {
        public Node? Zero, One;
        public int Run = -1;
    }

    private static readonly Node WhiteTree = BuildTree(black: false);
    private static readonly Node BlackTree = BuildTree(black: true);

    private static Node BuildTree(bool black)
    {
        var root = new Node();
        foreach (var (code, run) in T4Codes.RunCodes(black))
        {
            var n = root;
            foreach (char c in code)
                n = c == '0' ? n.Zero ??= new Node() : n.One ??= new Node();
            if (n.Run >= 0) throw new InvalidOperationException($"Duplicate code {code}");
            n.Run = run;
        }
        return root;
    }

    public T4DecodeResult DecodeT4(ReadOnlySpan<byte> data, int width, FaxResolution resolution, FaxCompression compression, bool lsbFirst)
    {
        var reader = new BitReader(data, lsbFirst);
        var rows = new List<byte[]>();
        var reference = new byte[width];
        int good = 0, bad = 0, consecutiveBad = 0, maxConsecutiveBad = 0;
        bool rtc = false;

        if (!reader.SkipToNextEol())
            return new T4DecodeResult(new FaxBitmap(width, 0, resolution), 0, 0, 0, false);

        while (!reader.AtEnd)
        {
            // Positioned just after an EOL.
            int tag = compression == FaxCompression.MR ? reader.ReadBit() : 1;
            if (tag < 0) break;

            if (reader.AtEol())
            {
                // Two EOLs with no line data between them: the start of RTC. Leading duplicate EOLs before the first line are skipped.
                if (rows.Count > 0) { rtc = true; break; }
                reader.SkipToNextEol();
                continue;
            }

            var line = new byte[width];
            bool ok;
            long start = reader.Position;
            try
            {
                if (tag == 1) Decode1D(reader, line);
                else Decode2D(reader, reference, line);
                ok = reader.AtEol() || reader.Remaining < 12;
            }
            catch (FaxDecodeException)
            {
                ok = false;
            }

            if (ok)
            {
                good++;
                consecutiveBad = 0;
                rows.Add(line);
                reference = line;
            }
            else
            {
                bad++;
                consecutiveBad++;
                maxConsecutiveBad = Math.Max(maxConsecutiveBad, consecutiveBad);
                // Conceal: repeat the previous line. The 2D reference stays at the last good line.
                rows.Add((byte[])reference.Clone());
                reader.Position = start;
            }

            if (!reader.SkipToNextEol()) break;
        }

        var bitmap = PackRows(rows, width, resolution);
        return new T4DecodeResult(bitmap, good, bad, maxConsecutiveBad, rtc);
    }

    /// <summary>Decodes T.6 data of known dimensions (MSB-first unless <paramref name="lsbFirst"/>).</summary>
    public FaxBitmap DecodeT6(ReadOnlySpan<byte> data, int width, int height, FaxResolution resolution, bool lsbFirst = false)
    {
        var reader = new BitReader(data, lsbFirst);
        var bitmap = new FaxBitmap(width, height, resolution);
        var reference = new byte[width];
        var line = new byte[width];
        for (int y = 0; y < height; y++)
        {
            Decode2D(reader, reference, line);
            Pack(line, bitmap.Row(y));
            (reference, line) = (line, reference);
        }
        return bitmap;
    }

    /// <summary>Decodes Modified Huffman data without EOLs (TIFF compression 2 style is not supported; this is T.4 1D with optional EOLs).</summary>
    public FaxBitmap DecodeMhRows(ReadOnlySpan<byte> data, int width, int height, FaxResolution resolution, bool lsbFirst = false)
    {
        var reader = new BitReader(data, lsbFirst);
        var bitmap = new FaxBitmap(width, height, resolution);
        var line = new byte[width];
        for (int y = 0; y < height; y++)
        {
            if (reader.AtEol()) reader.SkipToNextEol();
            Array.Clear(line);
            Decode1D(reader, line);
            Pack(line, bitmap.Row(y));
        }
        return bitmap;
    }

    private static int ReadRun(BitReader reader, bool black)
    {
        int total = 0;
        while (true)
        {
            var node = black ? BlackTree : WhiteTree;
            int length = 0;
            while (node.Run < 0)
            {
                int bit = reader.ReadBit();
                if (bit < 0) throw new FaxDecodeException("Unexpected end of data.");
                node = (bit == 0 ? node.Zero : node.One) ?? throw new FaxDecodeException("Invalid run-length code.");
                if (++length > 13) throw new FaxDecodeException("Invalid run-length code.");
            }
            total += node.Run;
            if (node.Run < 64) return total;
        }
    }

    internal static void Decode1D(BitReader reader, byte[] line)
    {
        int width = line.Length;
        int pos = 0;
        bool black = false;
        while (pos < width)
        {
            int run = ReadRun(reader, black);
            if (pos + run > width) throw new FaxDecodeException("Line too long.");
            if (black) line.AsSpan(pos, run).Fill(1);
            else line.AsSpan(pos, run).Clear();
            pos += run;
            black = !black;
        }
    }

    private enum Mode { Pass, Horizontal, V0, VR1, VR2, VR3, VL1, VL2, VL3 }

    private static Mode ReadMode(BitReader reader)
    {
        // Codes: 1 V0 | 011 VR1 | 010 VL1 | 001 H | 0001 P | 000011 VR2 | 000010 VL2 | 0000011 VR3 | 0000010 VL3
        int b = Next(reader);
        if (b == 1) return Mode.V0;
        b = Next(reader);
        if (b == 1) return Next(reader) == 1 ? Mode.VR1 : Mode.VL1;
        b = Next(reader);
        if (b == 1) return Mode.Horizontal;
        b = Next(reader);
        if (b == 1) return Mode.Pass;
        b = Next(reader);
        if (b == 1) return Next(reader) == 1 ? Mode.VR2 : Mode.VL2;
        b = Next(reader);
        if (b == 1) return Next(reader) == 1 ? Mode.VR3 : Mode.VL3;
        throw new FaxDecodeException("Unsupported 2D code (extension or EOL inside line).");

        static int Next(BitReader r)
        {
            int bit = r.ReadBit();
            if (bit < 0) throw new FaxDecodeException("Unexpected end of data.");
            return bit;
        }
    }

    internal static void Decode2D(BitReader reader, byte[] reference, byte[] line)
    {
        int width = line.Length;
        int a0 = -1;
        byte color = 0;
        while (a0 < width)
        {
            int b1 = T4Encoder.FindB1(reference, a0, color);
            int b2 = T4Encoder.NextChange(reference, b1);
            int start = Math.Max(a0, 0);
            switch (ReadMode(reader))
            {
                case Mode.Pass:
                    if (b2 > width || b2 <= a0) throw new FaxDecodeException("Invalid pass mode.");
                    line.AsSpan(start, b2 - start).Fill(color);
                    a0 = b2;
                    break;
                case Mode.Horizontal:
                {
                    int r1 = ReadRun(reader, color == 1);
                    int r2 = ReadRun(reader, color == 0);
                    if (start + r1 + r2 > width) throw new FaxDecodeException("Horizontal mode overflows line.");
                    line.AsSpan(start, r1).Fill(color);
                    line.AsSpan(start + r1, r2).Fill((byte)(color ^ 1));
                    a0 = start + r1 + r2;
                    break;
                }
                case var mode:
                {
                    int delta = mode switch
                    {
                        Mode.V0 => 0, Mode.VR1 => 1, Mode.VR2 => 2, Mode.VR3 => 3,
                        Mode.VL1 => -1, Mode.VL2 => -2, _ => -3,
                    };
                    int a1 = b1 + delta;
                    if (a1 > width || a1 < start || (a0 >= 0 && a1 <= a0))
                        throw new FaxDecodeException("Invalid vertical mode.");
                    line.AsSpan(start, a1 - start).Fill(color);
                    a0 = a1;
                    color ^= 1;
                    break;
                }
            }
        }
    }

    private static void Pack(byte[] line, Span<byte> row)
    {
        row.Clear();
        for (int x = 0; x < line.Length; x++)
            if (line[x] != 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
    }

    private static FaxBitmap PackRows(List<byte[]> rows, int width, FaxResolution resolution)
    {
        var bitmap = new FaxBitmap(width, rows.Count, resolution);
        for (int y = 0; y < rows.Count; y++) Pack(rows[y], bitmap.Row(y));
        return bitmap;
    }
}
