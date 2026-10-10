namespace PCActivityTracker.Services;

public static class UrlPrivacyService
{
    private const int MaximumStoredUrlLength = 4096;

    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        // SchemeAndServer intentionally excludes UriComponents.UserInfo. Query and
        // fragment are also omitted while the escaped path is retained.
        var sanitized = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        if (sanitized.Length <= MaximumStoredUrlLength) return sanitized;

        // Extremely long paths can otherwise inflate the database and UI. Retain
        // the origin rather than cutting an escape sequence into an invalid URL.
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}
