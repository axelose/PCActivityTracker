namespace PCActivityTracker.Services;

/// <summary>Accept only bounded identifiers belonging to the observed site's namespace.</summary>
public static class CreatorIdentityService
{
    public static string? Normalize(string? value, string domain)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl)) return null;
        var youtube = domain is "youtube.com" or "www.youtube.com" or "m.youtube.com";
        var twitch = domain is "twitch.tv" or "www.twitch.tv" or "m.twitch.tv";
        if (youtube && value.StartsWith("youtube:channel:", StringComparison.Ordinal))
        {
            var id = value["youtube:channel:".Length..];
            return id.Length == 24 && id.StartsWith("UC", StringComparison.Ordinal) &&
                id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? value : null;
        }
        if (youtube && value.StartsWith("youtube:handle:@", StringComparison.Ordinal))
        {
            var handle = value["youtube:handle:@".Length..];
            if (handle.Length is not (> 0 and <= 100) || handle.Any(character => char.IsWhiteSpace(character) || character is '/' or '\\' or ':' or '?' or '#')) return null;
            try { return "youtube:handle:@" + handle.Normalize().ToLowerInvariant(); }
            catch (ArgumentException) { return null; } // Invalid UTF-16 from an untrusted payload.
        }
        if (twitch && value.StartsWith("twitch:login:", StringComparison.Ordinal))
        {
            var login = value["twitch:login:".Length..];
            return login.Length is > 0 and <= 25 && login.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? "twitch:login:" + login.ToLowerInvariant() : null;
        }
        return null;
    }
}
