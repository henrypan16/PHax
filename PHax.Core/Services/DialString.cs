using System.Text;

namespace PHax.Core.Services;

public static class DialString
{
    /// <summary>Characters passed through to ATD: digits, * #, ',' (pause), W (wait for dial tone), '!' (flash).</summary>
    private const string Allowed = "0123456789*#,W!";

    /// <summary>Builds the dial string from the prefix and number. Formatting characters are dropped.</summary>
    public static string Build(string prefix, string number)
    {
        var sb = new StringBuilder();
        foreach (var c in (prefix ?? "") + (number ?? ""))
        {
            char u = char.ToUpperInvariant(c);
            if (Allowed.Contains(u)) sb.Append(u);
        }
        return sb.ToString();
    }

    public static bool IsValid(string number)
    {
        var digits = Build("", number).Count(char.IsDigit);
        return digits is >= 3 and <= 32;
    }

    /// <summary>Formats a T.30 station identifier: at most 20 characters of digits, '+' and space.</summary>
    public static string StationId(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value ?? "")
            if (char.IsAsciiDigit(c) || c is '+' or ' ') sb.Append(c);
        var s = sb.ToString().Trim();
        return s.Length > 20 ? s[..20] : s;
    }

    /// <summary>Masks all but the last four digits, for logs.</summary>
    public static string Redact(string number)
    {
        if (string.IsNullOrEmpty(number) || number.Length <= 4) return number;
        return new string('*', number.Length - 4) + number[^4..];
    }
}
