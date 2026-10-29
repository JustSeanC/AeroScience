using System.Text.RegularExpressions;
namespace AeroScience.Shared;

public static class ReleaseVersionComparison
{
    private static (Version Base, string[] Pre) Parse(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version)) throw new ArgumentException("Invalid semantic version.");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => p.All(char.IsDigit) && p.Length > 1 && p[0] == '0')) throw new ArgumentException("Numeric prerelease identifiers cannot have leading zeros.");
        return (version, pre);
    }
    public static int Compare(string left, string right)
    {
        var a = Parse(left); var b = Parse(right); var main = a.Base.CompareTo(b.Base); if (main != 0) return main;
        if (a.Pre.Length == 0 || b.Pre.Length == 0) return a.Pre.Length == b.Pre.Length ? 0 : a.Pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i]; var y = b.Pre[i]; var xn = x.All(char.IsDigit); var yn = y.All(char.IsDigit);
            var result = xn && yn ? x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y)
                : xn != yn ? xn ? -1 : 1 : string.CompareOrdinal(x, y);
            if (result != 0) return result;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
