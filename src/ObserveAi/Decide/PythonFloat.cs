using System.Globalization;

namespace ObserveAi;

/// <summary>
/// Formats a double as Python's f"{value:g}" and f"{value:.0f}" do, so derived-fact sentences stay
/// byte-identical to the measured output of eval/derived.py. .NET's "G6" uses a different threshold for
/// scientific notation and a different exponent width.
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
        // "E5" gives 6 correctly rounded significant digits, which is what :g rounds to first.
        var scientific = Math.Abs(value).ToString("E" + (Precision - 1), CultureInfo.InvariantCulture);
        var parts = scientific.Split('E');
        var digits = parts[0].Replace(".", "");
        var exponent = int.Parse(parts[1], CultureInfo.InvariantCulture);

        // Python's rule: fixed-point when -4 <= exponent < precision, scientific otherwise.
        var body = exponent is >= -4 and < Precision ? Fixed(digits, exponent) : Scientific(digits, exponent);
        return negative ? "-" + body : body;
    }

    /// <summary>Python's f"{value:.0f}": rounds half to even.</summary>
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
