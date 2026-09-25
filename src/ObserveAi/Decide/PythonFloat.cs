using System.Globalization;

namespace ObserveAi;

/// <summary>
/// Formats a double the way Python's f"{value:g}" and f"{value:.0f}" do, so the
/// derived-fact sentences built from these numbers stay byte-identical to
/// eval/derived.py's output, which is what was measured.
///
/// .NET's own "G6" format picks a different threshold for switching to
/// scientific notation and a different exponent width, so it cannot be used
/// as a drop-in replacement.
/// </summary>
internal static class PythonFloat
{
    private const int Precision = 6;

    /// <summary>Python's f"{value:g}": 6 significant digits, fixed or scientific.</summary>
    public static string FormatG(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        var negative = value < 0;
        // .NET's "E5" gives 6 significant digits (1 before the decimal point, 5
        // after), correctly rounded, which is exactly what :g rounds to first.
        var scientific = Math.Abs(value).ToString("E" + (Precision - 1), CultureInfo.InvariantCulture);
        var parts = scientific.Split('E');
        var digits = parts[0].Replace(".", "");
        var exponent = int.Parse(parts[1], CultureInfo.InvariantCulture);

        // Python's rule: fixed-point when -4 <= exponent < precision, scientific otherwise.
        var body = exponent is >= -4 and < Precision ? Fixed(digits, exponent) : Scientific(digits, exponent);
        return negative ? "-" + body : body;
    }

    /// <summary>Python's f"{value:.0f}": round to the nearest whole number.</summary>
    public static string FormatCount(double value) =>
        Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

    private static string Fixed(string digits, int exponent)
    {
        var pointPosition = exponent + 1;
        var result = pointPosition <= 0
            ? "0." + new string('0', -pointPosition) + digits
            : pointPosition >= digits.Length
                ? digits + new string('0', pointPosition - digits.Length)
                : digits[..pointPosition] + "." + digits[pointPosition..];
        return result.Contains('.') ? result.TrimEnd('0').TrimEnd('.') : result;
    }

    private static string Scientific(string digits, int exponent)
    {
        var fraction = digits[1..].TrimEnd('0');
        var mantissa = fraction.Length > 0 ? $"{digits[0]}.{fraction}" : digits[0].ToString();
        var sign = exponent < 0 ? "-" : "+";
        var magnitude = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        return $"{mantissa}e{sign}{magnitude}";
    }
}
