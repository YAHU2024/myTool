using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuickTranslate.Helpers;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class OpenAIWordLookupService : IWordLookupService, IDisposable
{
    internal const int MaxResponseBytes = 64 * 1024;
    private const int MaxHeadwordScalars = 128;
    private const int MaxRegionScalars = 16;
    private const int MaxPhoneticScalars = 128;
    private const int MaxPartOfSpeechScalars = 32;
    private const int MaxDefinitionScalars = 600;
    private const int MaxSentenceScalars = 500;
    private const int MaxSenses = 6;
    private const int MaxExamples = 3;
    private const int MaxCollocations = 3;
    private readonly HttpClient _httpClient;
    private WordLookupProviderSettings _settings;

    public OpenAIWordLookupService(WordLookupProviderSettings settings)
        : this(settings, new HttpClientHandler { UseProxy = false })
    {
    }

    internal OpenAIWordLookupService(
        WordLookupProviderSettings settings,
        HttpMessageHandler handler)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
    }

    public void UpdateSettings(WordLookupProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
    }

    public async Task<WordLookupResult> LookupAsync(
        WordLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = WordLookupPromptBuilder.NormalizeQuery(request.Query);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = Volatile.Read(ref _settings);
        var baseUrl = ApiEndpointValidator.ValidateAndNormalize(settings.ApiBaseUrl);
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("请先在设置中填写 API Key。");
        if (string.IsNullOrWhiteSpace(settings.ModelName))
            throw new InvalidOperationException("请先在设置中填写模型名称。");

        var language = string.IsNullOrWhiteSpace(request.ExplanationLanguage)
            ? settings.ExplanationLanguage
            : request.ExplanationLanguage;
        var prompt = WordLookupPromptBuilder.Build(language);
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.ModelName,
            ["messages"] = new[]
            {
                new { role = "system", content = prompt },
                new { role = "user", content = query }
            },
            ["temperature"] = 0.1,
            ["stream"] = false
        };
        if (baseUrl.Contains("bigmodel.cn", StringComparison.OrdinalIgnoreCase))
            body["thinking"] = new { type = "disabled" };
        else if (baseUrl.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            body["enable_thinking"] = false;

        var startedAt = Stopwatch.GetTimestamp();
        Logger.Info("WordLookupService", "lookup.started", new
        {
            query_scalars = query.EnumerateRunes().Count(),
            provider = "openai-compatible"
        });

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey}");

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Word lookup request failed ({(int)response.StatusCode}).");

        var responseBody = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var content = ExtractAssistantContent(responseBody);
        var result = ParseResult(content, settings.ModelName);
        Logger.Info("WordLookupService", "lookup.completed", new
        {
            provider = result.Source.ProviderId,
            senses = result.Senses.Count,
            examples = result.Examples.Count,
            collocations = result.Collocations.Count,
            duration_ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
        });
        return result;
    }

    internal static WordLookupResult ParseResult(string content, string modelName)
    {
        var json = StripSingleCodeFence(content);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new WordLookupFormatException("查词响应必须是 JSON 对象。");

            var status = RequiredString(root, "status", 16);
            if (status == "not_found")
                throw new WordLookupNotFoundException();
            if (status != "found")
                throw new WordLookupFormatException("查词响应状态无效。");

            var headword = RequiredString(root, "headword", MaxHeadwordScalars);
            var pronunciations = ParsePronunciations(root);
            var senses = ParseSenses(root);
            if (senses.Count == 0)
                throw new WordLookupFormatException("查词响应缺少释义。");
            var examples = ParseExamples(root);
            var collocations = ParseStringArray(
                root,
                "collocations",
                MaxCollocations,
                MaxDefinitionScalars);
            var model = RequiredValue(modelName, "模型名称", MaxHeadwordScalars);

            return new WordLookupResult(
                headword,
                pronunciations,
                senses,
                examples,
                collocations,
                new WordLookupSource(
                    "openai-compatible",
                    $"AI 释义 · {model}",
                    WordLookupSourceKind.AiGenerated));
        }
        catch (WordLookupNotFoundException)
        {
            throw;
        }
        catch (WordLookupFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new WordLookupFormatException("查词服务返回了无法解析的数据。", ex);
        }
    }

    private static IReadOnlyList<WordPronunciation> ParsePronunciations(JsonElement root)
    {
        var items = OptionalArray(root, "pronunciations", 4);
        return items.Select(item => new WordPronunciation(
            OptionalString(item, "region", MaxRegionScalars),
            RequiredString(item, "phonetic", MaxPhoneticScalars)))
            .ToArray();
    }

    private static IReadOnlyList<WordSense> ParseSenses(JsonElement root)
    {
        var items = OptionalArray(root, "senses", MaxSenses);
        return items.Select(item => new WordSense(
            OptionalString(item, "part_of_speech", MaxPartOfSpeechScalars),
            RequiredString(item, "definition", MaxDefinitionScalars),
            OptionalString(item, "english_definition", MaxDefinitionScalars)))
            .ToArray();
    }

    private static IReadOnlyList<WordExample> ParseExamples(JsonElement root)
    {
        var items = OptionalArray(root, "examples", MaxExamples);
        return items.Select(item => new WordExample(
            RequiredString(item, "sentence", MaxSentenceScalars),
            OptionalString(item, "translation", MaxSentenceScalars)))
            .ToArray();
    }

    private static IReadOnlyList<string> ParseStringArray(
        JsonElement root,
        string propertyName,
        int maxItems,
        int maxScalars)
    {
        return OptionalArray(root, propertyName, maxItems)
            .Select(item => item.ValueKind == JsonValueKind.String
                ? RequiredValue(item.GetString(), propertyName, maxScalars)
                : throw new WordLookupFormatException($"{propertyName} 包含无效项目。"))
            .ToArray();
    }

    private static IReadOnlyList<JsonElement> OptionalArray(
        JsonElement root,
        string propertyName,
        int maxItems)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return Array.Empty<JsonElement>();
        if (value.ValueKind != JsonValueKind.Array)
            throw new WordLookupFormatException($"{propertyName} 必须是数组。");
        if (value.GetArrayLength() > maxItems)
            throw new WordLookupFormatException($"{propertyName} 项目过多。");
        return value.EnumerateArray().ToArray();
    }

    private static string RequiredString(JsonElement root, string propertyName, int maxScalars)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new WordLookupFormatException($"查词响应缺少 {propertyName}。");
        return RequiredValue(value.GetString(), propertyName, maxScalars);
    }

    private static string OptionalString(JsonElement root, string propertyName, int maxScalars)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new WordLookupFormatException($"{propertyName} 必须是字符串。");
        var text = value.GetString()?.Trim() ?? string.Empty;
        ValidateLength(text, propertyName, maxScalars);
        return text;
    }

    private static string RequiredValue(string? value, string fieldName, int maxScalars)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new WordLookupFormatException($"{fieldName} 不能为空。");
        ValidateLength(text, fieldName, maxScalars);
        return text;
    }

    private static void ValidateLength(string text, string fieldName, int maxScalars)
    {
        if (text.EnumerateRunes().Count() > maxScalars)
            throw new WordLookupFormatException($"{fieldName} 超出长度限制。");
    }

    private static string ExtractAssistantContent(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new WordLookupFormatException("查词响应没有候选结果。");
            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
                throw new WordLookupFormatException("查词响应内容为空。");
            return content;
        }
        catch (WordLookupFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WordLookupFormatException("查词服务返回了无效的响应结构。", ex);
        }
    }

    private static string StripSingleCodeFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0 || !text.EndsWith("```", StringComparison.Ordinal))
            throw new WordLookupFormatException("查词响应代码围栏不完整。");
        return text[(firstLineEnd + 1)..^3].Trim();
    }

    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new WordLookupFormatException("查词响应超出大小限制。");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose() => _httpClient.Dispose();
}
