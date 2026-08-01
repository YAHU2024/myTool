using System.Net;

namespace QuickTranslate.Helpers;

/// <summary>
/// Validates and normalizes API endpoint Base URLs to prevent
/// credentials being sent over plaintext HTTP to remote hosts.
/// </summary>
public static class ApiEndpointValidator
{
    /// <summary>
    /// Validates that the Base URL uses HTTPS (or HTTP for loopback only),
    /// is an absolute well-formed URI, and returns the normalized form.
    /// </summary>
    /// <returns>The normalized Base URL (no trailing slash, no fragment/query).</returns>
    /// <exception cref="ArgumentException">The URL is invalid or insecure.</exception>
    public static string ValidateAndNormalize(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("API 地址不能为空。");

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException(
                "API 地址无效：请输入完整的 URL（例如 https://open.bigmodel.cn/api/paas/v4）。");

        var scheme = uri.Scheme.ToLowerInvariant();

        if (scheme != "http" && scheme != "https")
            throw new ArgumentException("API 地址仅支持 HTTP 或 HTTPS 协议。");

        if (scheme == "https")
            return Normalize(uri);

        // HTTP is only allowed for loopback addresses
        if (IsLoopbackHost(uri.Host))
            return Normalize(uri);

        throw new ArgumentException(
            "出于安全考虑，不允许对远程地址使用 HTTP 明文传输。\n" +
            "请使用 HTTPS 地址，或仅在本地开发时使用 http://localhost 等回环地址。");
    }

    /// <summary>
    /// Checks whether the URL is acceptable without throwing.
    /// Returns null on success, or an error message string on failure.
    /// </summary>
    public static string? Validate(string baseUrl)
    {
        try
        {
            ValidateAndNormalize(baseUrl);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    internal static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            return true;

        return false;
    }

    private static string Normalize(Uri uri)
    {
        // Rebuild the URL without trailing slash and without fragment/query.
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }
}
