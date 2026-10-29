namespace PHax.Imaging.Encoding;

/// <summary>Writes a bitstream MSB-first into a growable buffer.</summary>
public sealed class BitWriter
{
    private byte[] _buffer;
    private int _byteCount;
    private int _acc;
    private int _accBits;

    public BitWriter(int capacity = 16384) => _buffer = new byte[capacity];

    public long BitPosition => (long)_byteCount * 8 + _accBits;

    public void Write(int bits, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            _acc = (_acc << 1) | ((bits >> i) & 1);
            if (++_accBits == 8) Flush();
        }
    }

    public void Write(T4Codes.Code code) => Write(code.Bits, code.Length);

    public void WriteZeros(int count)
    {
        while (_accBits != 0 && count > 0) { Write(0, 1); count--; }
        while (count >= 8) { Append(0); count -= 8; }
        if (count > 0) Write(0, count);
    }

    /// <summary>Pads with zero bits to the next byte boundary.</summary>
    public void AlignToByte()
    {
        if (_accBits != 0) Write(0, 8 - _accBits);
    }

    public byte[] ToArray(bool lsbFirst = false)
    {
        AlignToByte();
        var result = _buffer.AsSpan(0, _byteCount).ToArray();
        if (lsbFirst) BitReverse.InPlace(result);
        return result;
    }

    private void Flush()
    {
        Append((byte)_acc);
        _acc = 0;
        _accBits = 0;
    }

    private void Append(byte b)
    {
        if (_byteCount == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_byteCount++] = b;
    }
}

/// <summary>Reads a bitstream MSB-first.</summary>
public sealed class BitReader
{
    private readonly byte[] _data;
    private readonly long _bitLength;
    private long _pos;

    public BitReader(ReadOnlySpan<byte> data, bool lsbFirst = false)
    {
        _data = data.ToArray();
        if (lsbFirst) BitReverse.InPlace(_data);
        _bitLength = (long)_data.Length * 8;
    }

    public long Position { get => _pos; set => _pos = value; }
    public bool AtEnd => _pos >= _bitLength;
    public long Remaining => _bitLength - _pos;

    /// <summary>Returns the next bit, or -1 at end of data.</summary>
    public int ReadBit()
    {
        if (_pos >= _bitLength) return -1;
        int bit = (_data[_pos >> 3] >> (7 - (int)(_pos & 7))) & 1;
        _pos++;
        return bit;
    }

    public int PeekBits(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            long p = _pos + i;
            int bit = p < _bitLength ? (_data[p >> 3] >> (7 - (int)(p & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }
        return value;
    }

    public void Skip(int count) => _pos = Math.Min(_bitLength, _pos + count);

    public void AlignToByte() => _pos = (_pos + 7) & ~7L;

    /// <summary>
    /// Advances past fill bits to the end of the next EOL (eleven or more zeros followed by a one).
    /// Returns false if no EOL is found.
    /// </summary>
    public bool SkipToNextEol()
    {
        int zeros = 0;
        while (true)
        {
            int bit = ReadBit();
            if (bit < 0) return false;
            if (bit == 0) zeros++;
            else if (zeros >= 11) return true;
            else zeros = 0;
        }
    }

    /// <summary>True if the next bits are an EOL, possibly preceded by fill zeros.</summary>
    public bool AtEol()
    {
        long p = _pos;
        int zeros = 0;
        while (p < _bitLength)
        {
            int bit = (_data[p >> 3] >> (7 - (int)(p & 7))) & 1;
            if (bit == 1) return zeros >= 11;
            zeros++;
            p++;
        }
        return false;
    }
}

public static class BitReverse
{
    public static readonly byte[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        int r = 0;
        for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) r |= 0x80 >> b;
        return (byte)r;
    }).ToArray();

    public static void InPlace(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++) data[i] = Table[data[i]];
    }
}
