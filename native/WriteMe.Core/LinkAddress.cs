using System.Text.RegularExpressions;

namespace WriteMe.Core;

public static partial class LinkAddress
{
    public static string? Normalize(string input)
    {
        var value = input.Trim();
        if (value.Length == 0 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return null;
        if (value.StartsWith("//")) value = "https:" + value;
        else if (!Scheme().IsMatch(value)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto" or "tel")) return null;
        if (uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme is "mailto" or "tel" && string.IsNullOrWhiteSpace(value[(value.IndexOf(':') + 1)..])) return null;
        return value;
    }
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex Scheme();
}
