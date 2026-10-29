using System.Text;

namespace PHax.Modem.T30;

/// <summary>
/// T.30 facsimile control field values as octets on a Class 1 modem (bit 1 of T.30 = LSB).
/// Bit 0x01 is the "X" bit: 1 in frames sent by the station that received DIS (normally the caller), 0 otherwise.
/// DIS/DTC, CSI/CIG and NSF/NSC differ only in that bit, so they are compared exactly.
/// </summary>
public static class Fcf
{
    public const byte X = 0x01;

    // Initial identification (answering station).
    public const byte DIS = 0x80, CSI = 0x40, NSF = 0x20;
    // Polling commands (bit 0 fixed to 1).
    public const byte DTC = 0x81, CIG = 0x41, NSC = 0x21;
    // Commands to send (X bit added by sender).
    public const byte DCS = 0x82, TSI = 0x42, NSS = 0x22, SUB = 0xC2, SID = 0xA2;
    // Pre-message responses.
    public const byte CFR = 0x84, FTT = 0x44;
    // Post-message commands.
    public const byte EOM = 0x8E, MPS = 0x4E, EOP = 0x2E, PRI_EOM = 0x9E, PRI_MPS = 0x5E, PRI_EOP = 0x3E;
    // Post-message responses.
    public const byte MCF = 0x8C, RTP = 0xCC, RTN = 0x4C, PIP = 0xAC, PIN = 0x2C;
    // Other line control.
    public const byte DCN = 0xFA, CRP = 0x1A;

    private static readonly byte[] ExactCompare = [DIS, CSI, NSF, DTC, CIG, NSC];

    /// <summary>Normalises a received FCF: clears the X bit except for the identification group.</summary>
    public static byte Normalize(byte fcf) => ExactCompare.Contains(fcf) ? fcf : (byte)(fcf & 0xFE);

    public static string Name(byte fcf) => Normalize(fcf) switch
    {
        DIS => "DIS", CSI => "CSI", NSF => "NSF", DTC => "DTC", CIG => "CIG", NSC => "NSC",
        DCS => "DCS", TSI => "TSI", NSS => "NSS", SUB => "SUB", SID => "SID",
        CFR => "CFR", FTT => "FTT",
        EOM => "EOM", MPS => "MPS", EOP => "EOP", PRI_EOM => "PRI-EOM", PRI_MPS => "PRI-MPS", PRI_EOP => "PRI-EOP",
        MCF => "MCF", RTP => "RTP", RTN => "RTN", PIP => "PIP", PIN => "PIN",
        DCN => "DCN", CRP => "CRP",
        _ => $"FCF 0x{fcf:X2}",
    };
}

/// <summary>A parsed T.30 HDLC frame (address 0xFF, control 0x03 / 0x13, FCF, FIF).</summary>
public sealed record T30Frame(byte RawFcf, byte[] Fif, bool Final)
{
    public byte Fcf => T30.Fcf.Normalize(RawFcf);
    public string Name => T30.Fcf.Name(RawFcf);

    public static byte[] Build(byte fcf, ReadOnlySpan<byte> fif, bool final)
    {
        var frame = new byte[3 + fif.Length];
        frame[0] = 0xFF;
        frame[1] = final ? (byte)0x13 : (byte)0x03;
        frame[2] = fcf;
        fif.CopyTo(frame.AsSpan(3));
        return frame;
    }

    public static T30Frame? TryParse(byte[] raw)
    {
        // Some modems (seen on a Conexant CX93001) pass the opening HDLC flag 0x7E through as data.
        int start = 0;
        while (start < raw.Length && raw[start] == 0x7E) start++;
        if (raw.Length - start < 3 || raw[start] != 0xFF || (raw[start + 1] & 0xEF) != 0x03) return null;
        return new T30Frame(raw[start + 2], raw[(start + 3)..], (raw[start + 1] & 0x10) != 0);
    }

    public override string ToString() => Fif.Length > 0 ? $"{Name} [{Convert.ToHexString(Fif)}]" : Name;
}

/// <summary>Station identifiers (CSI/TSI/CIG): 20 IA5 characters sent last-character-first.</summary>
public static class StationIdentifier
{
    public static byte[] Encode(string id)
    {
        var s = (id ?? "").PadRight(20)[..20];
        var fif = new byte[20];
        for (int i = 0; i < 20; i++) fif[i] = (byte)s[19 - i];
        return fif;
    }

    public static string Decode(ReadOnlySpan<byte> fif)
    {
        var sb = new StringBuilder();
        for (int i = fif.Length - 1; i >= 0; i--)
        {
            char c = (char)fif[i];
            sb.Append(c is >= ' ' and <= '~' ? c : ' ');
        }
        return sb.ToString().Trim();
    }
}
