namespace PHax.Modem.Class1;

/// <summary>HDLC frame check sequence (ITU-T T.30 5.3.7 / ISO 3309 CRC-16, reflected polynomial 0x8408).</summary>
public static class HdlcFcs
{
    /// <summary>CRC remainder of a frame that includes a correct FCS.</summary>
    public const ushort GoodResidue = 0xF0B8;

    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int b = 0; b < 8; b++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            t[i] = crc;
        }
        return t;
    }

    private static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>FCS to append to <paramref name="frame"/> (low byte first, as transmitted).</summary>
    public static ushort Compute(ReadOnlySpan<byte> frame) => (ushort)~Update(0xFFFF, frame);

    public static byte[] Append(ReadOnlySpan<byte> frame)
    {
        ushort fcs = Compute(frame);
        var result = new byte[frame.Length + 2];
        frame.CopyTo(result);
        result[^2] = (byte)(fcs & 0xFF);
        result[^1] = (byte)(fcs >> 8);
        return result;
    }

    /// <summary>True when <paramref name="frameWithFcs"/> ends with a correct FCS.</summary>
    public static bool Check(ReadOnlySpan<byte> frameWithFcs) =>
        frameWithFcs.Length >= 3 && Update(0xFFFF, frameWithFcs) == GoodResidue;
}

/// <summary>DLE shielding used for Class 1 data streams (T.31 section 8.3.1).</summary>
public static class Dle
{
    public const byte DLE = 0x10;
    public const byte ETX = 0x03;
    public const byte SUB = 0x1A;
    public const byte CAN = 0x18;

    /// <summary>Escapes data for transmission to the modem: every DLE is doubled. Does not add the terminator.</summary>
    public static byte[] Escape(ReadOnlySpan<byte> data)
    {
        int extra = 0;
        foreach (var b in data) if (b == DLE) extra++;
        var result = new byte[data.Length + extra];
        int j = 0;
        foreach (var b in data)
        {
            result[j++] = b;
            if (b == DLE) result[j++] = DLE;
        }
        return result;
    }

    public static readonly byte[] Terminator = [DLE, ETX];
}

/// <summary>Incrementally removes DLE shielding from modem output and detects DLE ETX.</summary>
public sealed class DleDecoder
{
    private bool _pendingDle;
    private readonly MemoryStream _output = new();

    public bool Complete { get; private set; }
    public long Length => _output.Length;

    /// <summary>Feeds one byte; returns true when DLE ETX has been seen.</summary>
    public bool Feed(byte b)
    {
        if (Complete) return true;
        if (_pendingDle)
        {
            _pendingDle = false;
            switch (b)
            {
                case Dle.ETX: Complete = true; return true;
                case Dle.DLE: _output.WriteByte(Dle.DLE); break;
                // T.31: <DLE><SUB> represents two DLE data octets.
                case Dle.SUB: _output.WriteByte(Dle.DLE); _output.WriteByte(Dle.DLE); break;
                // Other DLE pairs (e.g. <DLE><.>) are modem signals; they carry no data.
            }
            return false;
        }
        if (b == Dle.DLE) _pendingDle = true;
        else _output.WriteByte(b);
        return false;
    }

    public byte[] ToArray() => _output.ToArray();
}
