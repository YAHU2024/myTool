using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QuickTranslate.Services;

internal static class ProviderHttpError
{
    internal const int MaxErrorBodyBytes = 8 * 1024;
    internal const int MaxMessageCharacters = 300;

    public static async Task<HttpRequestException> CreateExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(response);

        var providerMessage = await TryReadMessageAsync(
            response.Content,
            cancellationToken).ConfigureAwait(false);
        var message = $"{operation} request failed ({(int)response.StatusCode})";
        if (!string.IsNullOrWhiteSpace(providerMessage))
            message += $": {providerMessage}";

        return new HttpRequestException(message, inner: null, response.StatusCode);
    }

    internal static async Task<string?> TryReadMessageAsync(
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        if (content is null)
            return null;

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            while (buffer.Length < MaxErrorBodyBytes)
            {
                var remaining = MaxErrorBodyBytes - (int)buffer.Length;
                var read = await stream.ReadAsync(
                    chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
                return null;

            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (TryGetString(root, "message", out var message))
                return Sanitize(message);
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return Sanitize(error.GetString());
                if (error.ValueKind == JsonValueKind.Object &&
                    TryGetString(error, "message", out message))
                    return Sanitize(message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = new StringBuilder(Math.Min(value.Length, MaxMessageCharacters));
        var previousWasWhitespace = false;
        foreach (var character in value.Trim())
        {
            var isWhitespace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isWhitespace)
            {
                if (!previousWasWhitespace && normalized.Length > 0)
                    normalized.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            if (normalized.Length >= MaxMessageCharacters)
                break;
            normalized.Append(character);
            previousWasWhitespace = false;
        }

        return normalized.ToString().TrimEnd();
    }
}
