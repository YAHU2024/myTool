using QuickTranslate.Helpers;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ApiEndpointValidatorTests
{
    // ==================== HTTPS (always allowed) ====================

    [Fact]
    public void ValidateAndNormalize_AllowsHttpsPublicDomain()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://open.bigmodel.cn/api/paas/v4");
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", result);
    }

    [Fact]
    public void ValidateAndNormalize_AllowsHttpsWithPort()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://api.example.com:8443/v1");
        Assert.Equal("https://api.example.com:8443/v1", result);
    }

    // ==================== HTTP loopback (allowed) ====================

    [Fact]
    public void ValidateAndNormalize_AllowsHttpLocalhost()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "http://localhost:11434/api/chat");
        Assert.Equal("http://localhost:11434/api/chat", result);
    }

    [Fact]
    public void ValidateAndNormalize_AllowsHttpLoopbackV4()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "http://127.0.0.1:8080/v1");
        Assert.Equal("http://127.0.0.1:8080/v1", result);
    }

    [Fact]
    public void ValidateAndNormalize_AllowsHttpLoopbackV6()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "http://[::1]:8080/v1");
        Assert.Equal("http://[::1]:8080/v1", result);
    }

    [Fact]
    public void ValidateAndNormalize_AllowsHttpLocalhostWithoutPort()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "http://localhost/api");
        Assert.Equal("http://localhost/api", result);
    }

    // ==================== HTTP non-loopback (rejected) ====================

    [Fact]
    public void ValidateAndNormalize_RejectsHttpPublicDomain()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(
                "http://open.bigmodel.cn/api/paas/v4"));
        Assert.Contains("HTTP", ex.Message);
    }

    [Fact]
    public void ValidateAndNormalize_RejectsHttpLanIp()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(
                "http://192.168.1.100/api"));
        Assert.Contains("HTTP", ex.Message);
    }

    [Fact]
    public void ValidateAndNormalize_RejectsHttpPublicIp()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(
                "http://203.0.113.5/api"));
        Assert.Contains("HTTP", ex.Message);
    }

    [Fact]
    public void ValidateAndNormalize_RejectsHttpInternalHostname()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(
                "http://my-internal-server.local/api"));
        Assert.Contains("HTTP", ex.Message);
    }

    // ==================== Empty / whitespace / relative (rejected) ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public void ValidateAndNormalize_RejectsEmptyOrWhitespace(string? url)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(url!));
        Assert.Contains("不能为空", ex.Message);
    }

    [Theory]
    [InlineData("/api/v1")]
    [InlineData("api/v1")]
    [InlineData("//example.com/api")]
    public void ValidateAndNormalize_RejectsRelativeUrl(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(url));
    }

    // ==================== Invalid URI (rejected) ====================

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("http:///missing-host")]
    public void ValidateAndNormalize_RejectsUnparseableUrl(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(url));
    }

    // ==================== Non-HTTP scheme (rejected) ====================

    [Theory]
    [InlineData("ftp://example.com/api")]
    [InlineData("ws://example.com/api")]
    [InlineData("file:///etc/passwd")]
    public void ValidateAndNormalize_RejectsNonHttpScheme(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ApiEndpointValidator.ValidateAndNormalize(url));
        Assert.Contains("仅支持", ex.Message);
    }

    // ==================== Normalization ====================

    [Fact]
    public void ValidateAndNormalize_StripsTrailingSlash()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://api.openai.com/v1/");
        Assert.Equal("https://api.openai.com/v1", result);
    }

    [Fact]
    public void ValidateAndNormalize_StripsMultipleTrailingSlashes()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://api.openai.com/v1///");
        Assert.Equal("https://api.openai.com/v1", result);
    }

    [Fact]
    public void ValidateAndNormalize_StripsQueryAndFragment()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://api.openai.com/v1/?key=val#section");
        Assert.Equal("https://api.openai.com/v1", result);
    }

    [Fact]
    public void ValidateAndNormalize_PreservesPathSegments()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "https://api.deepseek.com/beta/v1/chat");
        Assert.Equal("https://api.deepseek.com/beta/v1/chat", result);
    }

    [Fact]
    public void ValidateAndNormalize_HandlesWhitespaceAroundUrl()
    {
        var result = ApiEndpointValidator.ValidateAndNormalize(
            "  https://api.example.com/v1  ");
        Assert.Equal("https://api.example.com/v1", result);
    }

    // ==================== Validate (non-throwing) ====================

    [Fact]
    public void Validate_ReturnsNull_ForValidUrl()
    {
        var error = ApiEndpointValidator.Validate("https://api.example.com/v1");
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ReturnsMessage_ForInvalidUrl()
    {
        var error = ApiEndpointValidator.Validate("http://public.example.com/api");
        Assert.NotNull(error);
        Assert.Contains("HTTP", error);
    }

    [Fact]
    public void Validate_ReturnsMessage_ForEmptyUrl()
    {
        var error = ApiEndpointValidator.Validate("");
        Assert.NotNull(error);
        Assert.Contains("不能为空", error);
    }

    // ==================== IsLoopbackHost ====================

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("LocalHost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("0:0:0:0:0:0:0:1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("example.com", false)]
    [InlineData("8.8.8.8", false)]
    public void IsLoopbackHost_ReturnsExpected(string host, bool expected)
    {
        var result = ApiEndpointValidator.IsLoopbackHost(host);
        Assert.Equal(expected, result);
    }
}
