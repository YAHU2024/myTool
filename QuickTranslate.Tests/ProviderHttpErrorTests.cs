using System.Net;
using System.Text;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ProviderHttpErrorTests
{
    [Theory]
    [InlineData("{\"message\":\"Unsupported parameter\"}", "Unsupported parameter")]
    [InlineData("{\"error\":{\"message\":\"Model does not exist\"}}", "Model does not exist")]
    [InlineData("{\"error\":\"Invalid request\"}", "Invalid request")]
    public async Task TryReadMessageAsync_ExtractsKnownErrorShapes(string json, string expected)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var result = await ProviderHttpError.TryReadMessageAsync(content, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task TryReadMessageAsync_NormalizesWhitespaceAndLimitsLength()
    {
        var message = "first\r\nsecond " + new string('x', ProviderHttpError.MaxMessageCharacters + 20);
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { message }),
            Encoding.UTF8,
            "application/json");

        var result = await ProviderHttpError.TryReadMessageAsync(content, CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("first second ", result);
        Assert.True(result.Length <= ProviderHttpError.MaxMessageCharacters);
    }

    [Fact]
    public async Task CreateExceptionAsync_IncludesStatusAndSafeProviderMessage()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"thinking is unsupported\"}}",
                Encoding.UTF8,
                "application/json")
        };

        var exception = await ProviderHttpError.CreateExceptionAsync(
            "translation",
            response,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("translation request failed (400): thinking is unsupported", exception.Message);
    }

    [Fact]
    public async Task TryReadMessageAsync_ReturnsNullForNonJsonBody()
    {
        using var content = new StringContent("upstream failed", Encoding.UTF8, "text/plain");

        var result = await ProviderHttpError.TryReadMessageAsync(content, CancellationToken.None);

        Assert.Null(result);
    }
}
