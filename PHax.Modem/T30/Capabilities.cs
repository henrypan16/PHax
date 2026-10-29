using PHax.Core.Models;
using PHax.Modem.Class1;

namespace PHax.Modem.T30;

/// <summary>Decoded DIS/DTC capabilities of a remote receiver (T.30 Table 2).</summary>
public sealed record DisInfo
{
    public bool ReadyToTransmit { get; init; }
    public bool CanReceive { get; init; }
    public IReadOnlyList<FaxModulation> Modulations { get; init; } = [];
    public bool Fine { get; init; }
    public bool TwoDimensional { get; init; }
    /// <summary>Bit 17 = 255 mm (B4, code 1), bit 18 = 303 mm (A3, code 2); 0 = 1728 pels (A4) only.</summary>
    public int WidthCode { get; init; }
    /// <summary>Bit 19 = A4 and B4 (code 1), bit 20 = unlimited (code 2); 0 = A4 297 mm only.</summary>
    public int LengthCode { get; init; }
    /// <summary>Minimum scan line time in ms at standard resolution (conservative interpretation of bits 21-23).</summary>
    public int MinScanLineMs { get; init; }
    public bool Ecm { get; init; }
    public bool T6 { get; init; }

    public override string ToString() =>
        $"rx={CanReceive} mods=[{string.Join(", ", Modulations.Select(m => m.Name))}] fine={Fine} MR={TwoDimensional} " +
        $"width={WidthCode} length={LengthCode} minScan={MinScanLineMs}ms ECM={Ecm}";
}

/// <summary>Decoded DCS (parameters chosen by the transmitter).</summary>
public sealed record DcsInfo(FaxModulation Modulation, bool Fine, bool TwoDimensional, int WidthCode, int LengthCode, int MinScanLineMs, bool Ecm)
{
    public FaxResolution Resolution => Fine ? FaxResolution.Fine : FaxResolution.Standard;
    public FaxCompression Compression => TwoDimensional ? FaxCompression.MR : FaxCompression.MH;
    public int Width => WidthCode switch { 1 => 2048, 2 => 2432, _ => 1728 };

    public override string ToString() =>
        $"{Modulation.Name} {(Fine ? "fine" : "standard")} {(TwoDimensional ? "MR" : "MH")} width={Width} minScan={MinScanLineMs}ms";
}

/// <summary>
/// DIS/DCS bit-field codec. T.30 bit n of the FIF is octet (n-1)/8, mask 1 &lt;&lt; ((n-1) % 8), because Class 1
/// modems transmit each octet least-significant bit first.
/// </summary>
public static class Capabilities
{
    internal static bool Bit(ReadOnlySpan<byte> fif, int n)
    {
        int i = (n - 1) / 8;
        return i < fif.Length && (fif[i] & (1 << ((n - 1) % 8))) != 0;
    }

    internal static void SetBit(Span<byte> fif, int n, bool value)
    {
        int i = (n - 1) / 8;
        if (value) fif[i] |= (byte)(1 << ((n - 1) % 8));
        else fif[i] &= (byte)~(1 << ((n - 1) % 8));
    }

    /// <summary>Bits 11-14 as a 4-bit value (bit 11 = LSB).</summary>
    private static int RateBits(ReadOnlySpan<byte> fif) => fif.Length > 1 ? (fif[1] >> 2) & 0x0F : 0;

    private static int MinScanCode(ReadOnlySpan<byte> fif) =>
        (Bit(fif, 21) ? 4 : 0) | (Bit(fif, 22) ? 2 : 0) | (Bit(fif, 23) ? 1 : 0);

    public static DisInfo ParseDis(ReadOnlySpan<byte> fif)
    {
        int rate = RateBits(fif);
        bool v27 = (rate & 0b0010) != 0, v29 = (rate & 0b0001) != 0, v17 = (rate & 0b1000) != 0 && v29 && v27;
        var mods = new List<FaxModulation>();
        if (v17) mods.AddRange([FaxModulation.V17_14400, FaxModulation.V17_12000, FaxModulation.V17_9600, FaxModulation.V17_7200]);
        if (v29) mods.AddRange([FaxModulation.V29_9600, FaxModulation.V29_7200]);
        if (v27) mods.Add(FaxModulation.V27_4800);
        mods.Add(FaxModulation.V27_2400);

        // Bits (21,22,23): 000=20 001=40 010=10 100=5 011=10/half 110=20/half 101=40/half 111=0. Halved values are
        // ignored (longer fill is always acceptable).
        int ms = MinScanCode(fif) switch { 0b000 => 20, 0b001 => 40, 0b010 => 10, 0b100 => 5, 0b011 => 10, 0b110 => 20, 0b101 => 40, _ => 0 };

        return new DisInfo
        {
            ReadyToTransmit = Bit(fif, 9),
            CanReceive = Bit(fif, 10),
            Modulations = mods,
            Fine = Bit(fif, 15),
            TwoDimensional = Bit(fif, 16),
            WidthCode = (Bit(fif, 17) ? 1 : 0) | (Bit(fif, 18) ? 2 : 0),
            LengthCode = (Bit(fif, 19) ? 1 : 0) | (Bit(fif, 20) ? 2 : 0),
            MinScanLineMs = ms,
            Ecm = Bit(fif, 24) && Bit(fif, 27),
            T6 = Bit(fif, 24) && Bit(fif, 31),
        };
    }

    /// <summary>Builds our DIS: receive capability, the modulations we can receive, fine, MR, A4 width, unlimited length, 0 ms scan time.</summary>
    public static byte[] BuildDis(IEnumerable<FaxModulation> receivable, bool fine, bool mr)
    {
        var fif = new byte[3];
        var set = receivable.Select(m => m.Family).ToHashSet();
        int rate = 0;
        if (set.Contains(ModulationFamily.V27ter)) rate |= 0b0010;
        if (set.Contains(ModulationFamily.V29)) rate |= 0b0001;
        if (set.Contains(ModulationFamily.V17) && set.Contains(ModulationFamily.V29) && set.Contains(ModulationFamily.V27ter)) rate |= 0b1000;
        fif[1] = (byte)(rate << 2);
        SetBit(fif, 10, true);
        SetBit(fif, 15, fine);
        SetBit(fif, 16, mr);
        // Width: A4 only. Length: bit 20, unlimited. Minimum scan time 111 (0 ms). No extension.
        SetBit(fif, 20, true);
        SetBit(fif, 21, true);
        SetBit(fif, 22, true);
        SetBit(fif, 23, true);
        return fif;
    }

    public static byte[] BuildDcs(FaxModulation modulation, bool fine, bool mr, int minScanMs)
    {
        var fif = new byte[3];
        fif[1] = (byte)(modulation.DcsRateBits << 2);
        SetBit(fif, 10, true);
        SetBit(fif, 15, fine);
        SetBit(fif, 16, mr);
        // Width 00 = 1728; length 00 = A4 (Letter pages fit within A4 length).
        int code = minScanMs switch { >= 40 => 0b001, >= 20 => 0b000, >= 10 => 0b010, >= 5 => 0b100, _ => 0b111 };
        SetBit(fif, 21, (code & 4) != 0);
        SetBit(fif, 22, (code & 2) != 0);
        SetBit(fif, 23, (code & 1) != 0);
        return fif;
    }

    public static DcsInfo? ParseDcs(ReadOnlySpan<byte> fif)
    {
        var modulation = FaxModulation.FromDcsBits(RateBits(fif));
        if (modulation is null) return null;
        int ms = MinScanCode(fif) switch { 0b000 => 20, 0b001 => 40, 0b010 => 10, 0b100 => 5, _ => 0 };
        return new DcsInfo(modulation, Bit(fif, 15), Bit(fif, 16),
            (Bit(fif, 17) ? 1 : 0) | (Bit(fif, 18) ? 2 : 0),
            (Bit(fif, 19) ? 1 : 0) | (Bit(fif, 20) ? 2 : 0),
            ms, Bit(fif, 24) && Bit(fif, 27));
    }

    /// <summary>Maps the modem's +FTM/+FRM list to modulations (a family is usable if its long-train code is listed).</summary>
    public static IReadOnlyList<FaxModulation> FromModemCodes(IEnumerable<int> codes)
    {
        var set = codes.ToHashSet();
        return FaxModulation.All.Where(m => set.Contains(m.LongTrainCode)).ToList();
    }
}
