using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MerchantCrmBridge.Plugins.Logic;

/// <summary>Checks a merchant row must pass before anyone else sees it: a wrong IBAN found at settlement
/// costs a failed payout and a support case.</summary>
public static class Rules
{
    // IBAN lengths for the countries a merchant may settle in (EEA, Switzerland, United Kingdom).
    private static readonly Dictionary<string, int> IbanLength = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18,
        ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GR"] = 27, ["HR"] = 21, ["HU"] = 28,
        ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MT"] = 31,
        ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24,
    };

    public static IEnumerable<string> Countries => IbanLength.Keys;

    public static string NormalizeIban(string iban)
    {
        var sb = new StringBuilder(iban.Length);
        foreach (var ch in iban)
        {
            if (ch == ' ' || ch == '-') continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Null when valid, otherwise the reason.</summary>
    public static string? CheckIban(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "IBAN is missing.";
        var iban = NormalizeIban(raw!);
        if (iban.Length < 15) return "IBAN is too short.";
        var country = iban.Substring(0, 2);
        if (!IbanLength.TryGetValue(country, out var expected)) return "IBAN country " + country + " is not a settlement country.";
        if (iban.Length != expected) return "IBAN for " + country + " must have " + expected.ToString(CultureInfo.InvariantCulture) + " characters.";
        foreach (var ch in iban)
        {
            if (!(ch >= 'A' && ch <= 'Z') && !(ch >= '0' && ch <= '9')) return "IBAN contains an invalid character.";
        }
        // ISO 13616: move the first four characters to the end, letters to numbers, remainder mod 97 must be 1.
        var rearranged = iban.Substring(4) + iban.Substring(0, 4);
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var digits = ch >= 'A' ? (ch - 'A' + 10).ToString(CultureInfo.InvariantCulture) : ch.ToString();
            foreach (var d in digits) remainder = (remainder * 10 + (d - '0')) % 97;
        }
        return remainder == 1 ? null : "IBAN check digits are wrong.";
    }

    public static string? CheckMcc(string? mcc)
    {
        if (string.IsNullOrEmpty(mcc)) return "Merchant category code is missing.";
        if (mcc!.Length != 4) return "Merchant category code must have four digits.";
        foreach (var ch in mcc) if (ch < '0' || ch > '9') return "Merchant category code must have four digits.";
        return null;
    }

    public static string? CheckCountry(string? country)
    {
        if (string.IsNullOrEmpty(country)) return "Country is missing.";
        return IbanLength.ContainsKey(country!) ? null : "Country " + country + " is not supported.";
    }

    /// <summary>Merchant ids are M- followed by six digits; the platform uses them as its key.</summary>
    public static string? CheckMerchantId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "Merchant ID is missing.";
        if (id!.Length != 8 || id[0] != 'M' || id[1] != '-') return "Merchant ID must look like M-000123.";
        for (var i = 2; i < id.Length; i++) if (id[i] < '0' || id[i] > '9') return "Merchant ID must look like M-000123.";
        return null;
    }

    public static string? CheckEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "Contact email is missing.";
        var at = email!.IndexOf('@');
        if (at < 1 || at != email.LastIndexOf('@') || email.IndexOf('.', at) < at + 2 || email.EndsWith(".", StringComparison.Ordinal))
            return "Contact email is not valid.";
        return null;
    }
}
