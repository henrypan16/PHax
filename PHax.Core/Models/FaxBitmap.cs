namespace PHax.Core.Models;

/// <summary>
/// A bilevel fax page. Rows are packed MSB-first, one bit per pixel, 1 = black.
/// Horizontal resolution is always 204 dpi (8 pels/mm); vertical depends on <see cref="Resolution"/>.
/// </summary>
public sealed class FaxBitmap
{
    public const int A4Width = 1728;

    public int Width { get; }
    public int Height { get; private set; }
    public int Stride { get; }
    public FaxResolution Resolution { get; }
    public byte[] Data { get; private set; }

    public FaxBitmap(int width, int height, FaxResolution resolution)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Stride = (width + 7) / 8;
        Resolution = resolution;
        Data = new byte[Stride * height];
    }

    public FaxBitmap(int width, int height, FaxResolution resolution, byte[] data) : this(width, 0, resolution)
    {
        if (data.Length < (long)Stride * height)
            throw new ArgumentException("Pixel buffer too small.", nameof(data));
        Height = height;
        Data = data;
    }

    public double VerticalDpi => Resolution == FaxResolution.Fine ? 196 : 98;
    public const double HorizontalDpi = 204;

    public Span<byte> Row(int y) => Data.AsSpan(y * Stride, Stride);

    public bool GetPixel(int x, int y) => (Data[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    public void SetPixel(int x, int y, bool black)
    {
        ref byte b = ref Data[y * Stride + (x >> 3)];
        if (black) b |= (byte)(0x80 >> (x & 7));
        else b &= (byte)~(0x80 >> (x & 7));
    }

    /// <summary>Converts a fine page to standard resolution by OR-ing row pairs, which keeps thin horizontal strokes visible.</summary>
    public FaxBitmap ToStandard()
    {
        if (Resolution == FaxResolution.Standard) return this;
        var result = new FaxBitmap(Width, (Height + 1) / 2, FaxResolution.Standard);
        for (int y = 0; y < result.Height; y++)
        {
            var dst = result.Row(y);
            Row(2 * y).CopyTo(dst);
            if (2 * y + 1 < Height)
            {
                var second = Row(2 * y + 1);
                for (int i = 0; i < dst.Length; i++) dst[i] |= second[i];
            }
        }
        return result;
    }

    /// <summary>Converts a standard page to fine resolution by duplicating rows.</summary>
    public FaxBitmap ToFine()
    {
        if (Resolution == FaxResolution.Fine) return this;
        var result = new FaxBitmap(Width, Height * 2, FaxResolution.Fine);
        for (int y = 0; y < Height; y++)
        {
            Row(y).CopyTo(result.Row(2 * y));
            Row(y).CopyTo(result.Row(2 * y + 1));
        }
        return result;
    }

    /// <summary>Appends a row (used by decoders that do not know the height in advance).</summary>
    public static FaxBitmap FromRows(int width, FaxResolution resolution, IReadOnlyList<byte[]> rows)
    {
        var bmp = new FaxBitmap(width, rows.Count, resolution);
        for (int y = 0; y < rows.Count; y++) rows[y].AsSpan(0, bmp.Stride).CopyTo(bmp.Row(y));
        return bmp;
    }

    public bool ContentEquals(FaxBitmap other)
    {
        if (other.Width != Width || other.Height != Height) return false;
        int fullBytes = Width / 8;
        int tailBits = Width % 8;
        byte tailMask = (byte)(0xFF << (8 - tailBits));
        for (int y = 0; y < Height; y++)
        {
            var a = Row(y);
            var b = other.Row(y);
            if (!a[..fullBytes].SequenceEqual(b[..fullBytes])) return false;
            if (tailBits != 0 && (a[fullBytes] & tailMask) != (b[fullBytes] & tailMask)) return false;
        }
        return true;
    }
}
