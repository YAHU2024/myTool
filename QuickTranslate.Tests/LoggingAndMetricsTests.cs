using System.Text.Json;
using QuickTranslate.Core;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using QuickTranslate.Services;
using QuickTranslate.UI;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class LoggingAndMetricsTests
{
    [Fact]
    public void StructuredLog_RoundTripsWithoutContentFields()
    {
        var record = new LogEvent(
            DateTimeOffset.UtcNow,
            LogLevel.Info,
            "TranslationService",
            "translation.completed",
            new Dictionary<string, object?> { ["text_len"] = 12, ["duration_ms"] = 42.5 });

        var json = Logger.Serialize(record);

        Assert.DoesNotContain("secret text", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Logger.TryParse(json, out var parsed));
        Assert.Equal("translation.completed", parsed!.EventName);
        Assert.Equal("TranslationService", parsed.Source);
    }

    [Fact]
    public void PromptLogContext_ExcludesSourceAndPromptContent()
    {
        const string secretText = "secret selected text";
        const string secretPrompt = "secret custom prompt";
        var request = new TranslationRequest(
            TranslationRequestKind.Translation,
            secretText,
            "English",
            ContentType.Translation,
            "https://example.invalid/v1",
            "secret-api-key",
            "test-model",
            secretPrompt,
            FallbackUsed: true);

        var context = OpenAITranslationService.BuildPromptLogContext(
            request,
            customTranslationPrompt: true,
            customAnalysisPrompt: false,
            analysisPreset: "general");
        var json = Logger.Serialize(new LogEvent(
            DateTimeOffset.UtcNow,
            LogLevel.Debug,
            "TranslationService",
            "prompt.selected",
            context));

        Assert.Contains("prompt.selected", json, StringComparison.Ordinal);
        Assert.Contains("prompt_len", json, StringComparison.Ordinal);
        Assert.Contains("custom_translation_prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretPrompt, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-api-key", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionContext_RecordsTypeButNeverMessage()
    {
        const string secret = "Authorization: Bearer secret-user-content";

        var context = Logger.ExceptionContext(new InvalidOperationException(secret));
        var json = JsonSerializer.Serialize(context);

        Assert.Contains("InvalidOperationException", json, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogEntryReader_ParsesLegacyLogLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicktranslate-test-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "2026-07-22 12:34:56.789 [WRN] [App] legacy warning\n");
            var entries = LogEntryReader.Read(path);

            var entry = Assert.Single(entries);
            Assert.Equal(LogLevel.Warn, entry.Level);
            Assert.Equal("App", entry.Source);
            Assert.Equal("legacy warning", entry.EventName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TranslationMetrics_UsesBoundedPercentilesAndExcludesCacheLatency()
    {
        var metrics = new TranslationMetrics(windowSize: 3);
        metrics.RecordCompleted(TimeSpan.FromMilliseconds(10));
        metrics.RecordCompleted(TimeSpan.FromMilliseconds(20));
        metrics.RecordCompleted(TimeSpan.FromMilliseconds(30));
        metrics.RecordCompleted(TimeSpan.FromMilliseconds(40));
        metrics.RecordCompleted(TimeSpan.Zero, cacheHit: true);

        var snapshot = metrics.GetSnapshot(cacheHits: 1, cacheMisses: 4);

        Assert.Equal(5, snapshot.Completed);
        Assert.Equal(30, snapshot.AverageMilliseconds);
        Assert.Equal(30, snapshot.P50Milliseconds);
        Assert.Equal(39, snapshot.P95Milliseconds);
        Assert.Equal(39.8, snapshot.P99Milliseconds, precision: 1);
        Assert.Equal(0.2, snapshot.CacheHitRate);
    }

    [Fact]
    public void AppSettings_LogLimitsAreRepresentedInBytes()
    {
        var settings = new QuickTranslate.Models.AppSettings();
        Assert.Equal(7, settings.LogRetentionDays);
        Assert.Equal(50 * 1024 * 1024, settings.LogMaxTotalBytes);
    }

    [Fact]
    public void CleanupDirectory_RemovesExpiredAndOldestFilesButProtectsCurrentFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"quicktranslate-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, "quicktranslate-2026-01-01.log");
        var oldest = Path.Combine(directory, "quicktranslate-2026-07-20.log");
        var current = Path.Combine(directory, "quicktranslate-2026-07-22.log");
        try
        {
            File.WriteAllBytes(expired, new byte[400]);
            File.WriteAllBytes(oldest, new byte[400]);
            File.WriteAllBytes(current, new byte[400]);
            File.SetLastWriteTimeUtc(expired, new DateTime(2026, 1, 1));
            File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 7, 20));
            File.SetLastWriteTimeUtc(current, new DateTime(2026, 7, 22));

            Logger.CleanupDirectory(
                directory,
                retentionDays: 7,
                maxTotalBytes: 400,
                utcNow: new DateTime(2026, 7, 22),
                protectedPaths: new[] { current });

            Assert.False(File.Exists(expired));
            Assert.False(File.Exists(oldest));
            Assert.True(File.Exists(current));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
