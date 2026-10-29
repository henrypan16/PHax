namespace PHax.Modem.Class1;

public enum ModulationFamily { V27ter, V29, V17 }

/// <summary>
/// A high-speed fax modulation with its Class 1 carrier codes (T.31 Table 3) and DCS rate bits (T.30 Table 2, bits 11-14).
/// </summary>
public sealed record FaxModulation(ModulationFamily Family, int BitRate, int LongTrainCode, int ShortTrainCode, int DcsRateBits)
{
    public static readonly FaxModulation V17_14400 = new(ModulationFamily.V17, 14400, 145, 146, 0b1000);
    public static readonly FaxModulation V17_12000 = new(ModulationFamily.V17, 12000, 121, 122, 0b1010);
    public static readonly FaxModulation V17_9600 = new(ModulationFamily.V17, 9600, 97, 98, 0b1001);
    public static readonly FaxModulation V17_7200 = new(ModulationFamily.V17, 7200, 73, 74, 0b1011);
    public static readonly FaxModulation V29_9600 = new(ModulationFamily.V29, 9600, 96, 96, 0b0001);
    public static readonly FaxModulation V29_7200 = new(ModulationFamily.V29, 7200, 72, 72, 0b0011);
    public static readonly FaxModulation V27_4800 = new(ModulationFamily.V27ter, 4800, 48, 48, 0b0010);
    public static readonly FaxModulation V27_2400 = new(ModulationFamily.V27ter, 2400, 24, 24, 0b0000);

    /// <summary>Fallback order, fastest first.</summary>
    public static readonly IReadOnlyList<FaxModulation> All =
        [V17_14400, V17_12000, V17_9600, V17_7200, V29_9600, V29_7200, V27_4800, V27_2400];

    /// <summary>
    /// DCS rate bits are held here as a 4-bit value where bit 0 = T.30 bit 11 ... bit 3 = T.30 bit 14
    /// (so V.17 14400, "bits 11-14 = 0,0,0,1", is 0b1000).
    /// </summary>
    public static FaxModulation? FromDcsBits(int bits) => All.FirstOrDefault(m => m.DcsRateBits == bits);

    public string Name => $"{(Family == ModulationFamily.V27ter ? "V.27ter" : Family == ModulationFamily.V29 ? "V.29" : "V.17")} {BitRate}";

    /// <summary>Bytes per second of line time.</summary>
    public int BytesPerSecond => BitRate / 8;

    public override string ToString() => Name;
}
