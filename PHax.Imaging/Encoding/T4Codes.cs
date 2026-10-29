namespace PHax.Imaging.Encoding;

/// <summary>ITU-T T.4 code tables (Tables 1/T.4, 2/T.4, 3/T.4 and the 2D mode codes of Table 4/T.4).</summary>
public static class T4Codes
{
    public readonly record struct Code(int Bits, int Length);

    public const int EolBits = 0x001;
    public const int EolLength = 12;

    // Terminating codes, run lengths 0..63.
    public static readonly string[] WhiteTerminating =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111",
        "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
        "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000",
        "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000",
        "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    public static readonly string[] BlackTerminating =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011",
        "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
        "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111",
        "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
        "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    // Make-up codes for 64, 128, ... 1728.
    public static readonly string[] WhiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101",
        "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
        "010011010", "011000", "010011011",
    ];

    public static readonly string[] BlackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
        "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
        "0000001011011", "0000001100100", "0000001100101",
    ];

    // Extended make-up codes (shared by both colours) for 1792, 1856, ... 2560.
    public static readonly string[] ExtendedMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
        "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    // 2D mode codes.
    public static readonly Code Pass = Parse("0001");
    public static readonly Code Horizontal = Parse("001");
    /// <summary>Vertical codes indexed by (a1 - b1) + 3: VL3, VL2, VL1, V0, VR1, VR2, VR3.</summary>
    public static readonly Code[] Vertical =
    [
        Parse("0000010"), Parse("000010"), Parse("010"), Parse("1"), Parse("011"), Parse("000011"), Parse("0000011"),
    ];

    private static readonly Code[] WhiteTermCodes = WhiteTerminating.Select(Parse).ToArray();
    private static readonly Code[] BlackTermCodes = BlackTerminating.Select(Parse).ToArray();
    private static readonly Code[] WhiteMakeupCodes = WhiteMakeup.Select(Parse).ToArray();
    private static readonly Code[] BlackMakeupCodes = BlackMakeup.Select(Parse).ToArray();
    private static readonly Code[] ExtendedCodes = ExtendedMakeup.Select(Parse).ToArray();

    public static Code Terminating(bool black, int run) => (black ? BlackTermCodes : WhiteTermCodes)[run];

    /// <summary>Make-up code for a multiple of 64 between 64 and 2560.</summary>
    public static Code Makeup(bool black, int run)
    {
        int index = run / 64 - 1;
        if (run <= 1728) return (black ? BlackMakeupCodes : WhiteMakeupCodes)[index];
        return ExtendedCodes[(run - 1792) / 64];
    }

    public static Code Parse(string bits) => new(Convert.ToInt32(bits, 2), bits.Length);

    /// <summary>All run-length codes for one colour as (code, run) pairs; used to build decoders and in table tests.</summary>
    public static IEnumerable<(string Code, int Run)> RunCodes(bool black)
    {
        var term = black ? BlackTerminating : WhiteTerminating;
        var makeup = black ? BlackMakeup : WhiteMakeup;
        for (int i = 0; i < term.Length; i++) yield return (term[i], i);
        for (int i = 0; i < makeup.Length; i++) yield return (makeup[i], (i + 1) * 64);
        for (int i = 0; i < ExtendedMakeup.Length; i++) yield return (ExtendedMakeup[i], 1792 + i * 64);
    }
}
