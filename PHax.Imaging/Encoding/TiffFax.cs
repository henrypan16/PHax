using System.Buffers.Binary;
using PHax.Core.Models;
using PHax.Imaging.Decoding;

namespace PHax.Imaging.Encoding;

/// <summary>Multi-page bilevel TIFF writer (T.6 / Group 4) and reader (uncompressed, T.4, T.6).</summary>
public static class TiffFax
{
    private const ushort Short = 3, Long = 4, Rational = 5, Ascii = 2;

    public static void Write(string path, IReadOnlyList<FaxBitmap> pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) Write(fs, pages);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Write(Stream stream, IReadOnlyList<FaxBitmap> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        var encoder = new T4Encoder();
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write("II"u8);
        bw.Write((ushort)42);
        long nextIfdPointer = ms.Position;
        bw.Write(0u);

        var software = System.Text.Encoding.ASCII.GetBytes("PHax\0");
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var data = encoder.EncodeT6(page);
            Align(bw);
            long dataOffset = ms.Position;
            bw.Write(data);
            Align(bw);

            long ifdOffset = ms.Position;
            Patch(ms, nextIfdPointer, (uint)ifdOffset);

            var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
            {
                (254, Long, 1, 2),
                (256, Long, 1, (uint)page.Width),
                (257, Long, 1, (uint)page.Height),
                (258, Short, 1, 1),
                (259, Short, 1, 4),
                (262, Short, 1, 0),
                (266, Short, 1, 1),
                (273, Long, 1, (uint)dataOffset),
                (277, Short, 1, 1),
                (278, Long, 1, (uint)page.Height),
                (279, Long, 1, (uint)data.Length),
                (282, Rational, 1, 0),
                (283, Rational, 1, 0),
                (293, Long, 1, 0),
                (296, Short, 1, 2),
                (297, Short, 2, (uint)(i | (pages.Count << 16))),
                (305, Ascii, (uint)software.Length, 0),
            };
            long extraOffset = ifdOffset + 2 + entries.Count * 12 + 4;
            uint xResOffset = (uint)extraOffset, yResOffset = xResOffset + 8, softwareOffset = yResOffset + 8;

            bw.Write((ushort)entries.Count);
            foreach (var (tag, type, count, value) in entries)
            {
                uint v = tag switch { 282 => xResOffset, 283 => yResOffset, 305 => softwareOffset, _ => value };
                bw.Write(tag);
                bw.Write(type);
                bw.Write(count);
                if (type == Short && count == 1) { bw.Write((ushort)v); bw.Write((ushort)0); }
                else bw.Write(v);
            }
            nextIfdPointer = ms.Position;
            bw.Write(0u);
            bw.Write(204u); bw.Write(1u);
            bw.Write((uint)page.VerticalDpi); bw.Write(1u);
            bw.Write(software);
        }
        bw.Flush();
        ms.Position = 0;
        ms.CopyTo(stream);
    }

    public static List<FaxBitmap> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Read(bytes);
    }

    public static int CountPages(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return EnumerateIfds(bytes).Count();
    }

    public static List<FaxBitmap> Read(byte[] file)
    {
        var pages = new List<FaxBitmap>();
        bool little = file[0] == 'I';
        foreach (var ifd in EnumerateIfds(file))
        {
            uint Get(ushort tag, uint def = 0) => ifd.TryGetValue(tag, out var v) ? v[0] : def;
            int width = (int)Get(256);
            int height = (int)Get(257);
            uint compression = Get(259, 1);
            uint photometric = Get(262, 0);
            bool lsb = Get(266, 1) == 2;
            uint t4Options = Get(292, 0);
            var offsets = ifd.GetValueOrDefault((ushort)273) ?? throw new InvalidDataException("TIFF without strips.");
            var counts = ifd.GetValueOrDefault((ushort)279) ?? throw new InvalidDataException("TIFF without strip byte counts.");
            if (Get(258, 1) != 1) throw new NotSupportedException("Only bilevel TIFF pages are read natively.");
            var resolution = YResolution(file, ifd, little) > 150 ? FaxResolution.Fine : FaxResolution.Standard;

            using var stripData = new MemoryStream();
            for (int s = 0; s < offsets.Length; s++) stripData.Write(file, (int)offsets[s], (int)counts[s]);
            var data = stripData.ToArray();

            var decoder = new T4Decoder();
            FaxBitmap page = compression switch
            {
                1 => new FaxBitmap(width, height, resolution, data),
                4 => decoder.DecodeT6(data, width, height, resolution, lsb),
                3 when (t4Options & 1) == 0 => decoder.DecodeMhRows(data, width, height, resolution, lsb),
                3 => FromT4(decoder, data, width, height, resolution, lsb),
                _ => throw new NotSupportedException($"TIFF compression {compression} is not supported natively."),
            };
            // Our bitmaps use 1 = black. Photometric 1 (BlackIsZero) stores 1 = white.
            if (photometric == 1)
                for (int i = 0; i < page.Data.Length; i++) page.Data[i] = (byte)~page.Data[i];
            pages.Add(page);
        }
        return pages;
    }

    private static FaxBitmap FromT4(T4Decoder decoder, byte[] data, int width, int height, FaxResolution res, bool lsb)
    {
        var result = decoder.DecodeT4(data, width, res, FaxCompression.MR, lsb);
        if (result.Page.Height == height) return result.Page;
        var page = new FaxBitmap(width, height, res);
        for (int y = 0; y < Math.Min(height, result.Page.Height); y++) result.Page.Row(y).CopyTo(page.Row(y));
        return page;
    }

    private static double YResolution(byte[] file, Dictionary<ushort, uint[]> ifd, bool little)
    {
        if (!ifd.TryGetValue(283, out var off)) return 98;
        uint num = ReadU32(file, (int)off[0], little), den = ReadU32(file, (int)off[0] + 4, little);
        double dpi = den == 0 ? 98 : (double)num / den;
        if (ifd.TryGetValue(296, out var unit) && unit[0] == 3) dpi *= 2.54;
        return dpi;
    }

    private static IEnumerable<Dictionary<ushort, uint[]>> EnumerateIfds(byte[] file)
    {
        if (file.Length < 8) throw new InvalidDataException("Not a TIFF file.");
        bool little = file[0] == 'I' && file[1] == 'I';
        if (!little && !(file[0] == 'M' && file[1] == 'M')) throw new InvalidDataException("Not a TIFF file.");
        if (ReadU16(file, 2, little) != 42) throw new InvalidDataException("Not a TIFF file.");
        uint offset = ReadU32(file, 4, little);
        var seen = new HashSet<uint>();
        while (offset != 0 && offset < file.Length && seen.Add(offset))
        {
            int count = ReadU16(file, (int)offset, little);
            var tags = new Dictionary<ushort, uint[]>();
            for (int i = 0; i < count; i++)
            {
                int e = (int)offset + 2 + i * 12;
                ushort tag = ReadU16(file, e, little);
                ushort type = ReadU16(file, e + 2, little);
                uint n = ReadU32(file, e + 4, little);
                int size = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
                int valuePos = n * size <= 4 ? e + 8 : (int)ReadU32(file, e + 8, little);
                if (type == 5) { tags[tag] = [(uint)valuePos]; continue; }
                var values = new uint[Math.Min(n, 4096)];
                for (int k = 0; k < values.Length; k++)
                    values[k] = type == 3 ? ReadU16(file, valuePos + k * 2, little)
                        : type == 4 ? ReadU32(file, valuePos + k * 4, little)
                        : file[valuePos + k];
                tags[tag] = values;
            }
            yield return tags;
            offset = ReadU32(file, (int)offset + 2 + count * 12, little);
        }
    }

    private static ushort ReadU16(byte[] b, int pos, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pos)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos));

    private static uint ReadU32(byte[] b, int pos, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));

    private static void Align(BinaryWriter bw)
    {
        if ((bw.BaseStream.Position & 1) != 0) bw.Write((byte)0);
    }

    private static void Patch(MemoryStream ms, long position, uint value)
    {
        long save = ms.Position;
        ms.Position = position;
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        ms.Write(buf);
        ms.Position = save;
    }
}
