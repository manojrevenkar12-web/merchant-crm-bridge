using System.Globalization;
using System.Numerics;

namespace Bridge.Drills;

/// <summary>Valid German IBANs for invented accounts (check digits computed per ISO 13616).</summary>
public static class Ibans
{
    public static string German(long bankCode, long account)
    {
        var bban = bankCode.ToString("00000000", CultureInfo.InvariantCulture) + account.ToString("0000000000", CultureInfo.InvariantCulture);
        var numeric = BigInteger.Parse(bban + "131400", CultureInfo.InvariantCulture); // "DE00" -> D=13 E=14 00
        var check = 98 - (int)(numeric % 97);
        return "DE" + check.ToString("00", CultureInfo.InvariantCulture) + bban;
    }
}
