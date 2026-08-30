using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ScreenshotTranslationCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_UsesFakeOcrAndFakeTranslatorWithoutGuessingOrder()
    {
        var ocr = new FakeOcrService(new OcrResult(
            new[]
            {
                new OcrTextBlock("b0002", "地", new OcrBounds(20, 0, 10, 10)),
                new OcrTextBlock("b0001", "本", new OcrBounds(0, 0, 10, 10)),
                new OcrTextBlock("b0003", "line two", new OcrBounds(0, 40, 70, 10))
            },
            "zh-Hans-CN",
            false,
            0,
            TimeSpan.FromMilliseconds(1)));
        var coordinator = new ScreenshotTranslationCoordinator(ocr);
        var image = ValidImage();

        var result = await coordinator.ExecuteAsync(
            image,
            (units, _) => Task.FromResult<IReadOnlyList<TranslatedTextUnit>>(
                units.Reverse().Select(unit => new TranslatedTextUnit(unit.UnitId, $"T:{unit.SourceText}")).ToArray()));

        Assert.Equal(ScreenshotTranslationPipelineStatus.Completed, result.Status);
        Assert.True(result.Mapping.Accepted);
        Assert.Equal(new[] { "u0001", "u0002" }, result.Mapping.MappedUnits.Select(unit => unit.UnitId));
        Assert.Equal(3, result.Timings.OcrBlockCount);
        Assert.Equal(2, result.Timings.TranslationUnitCount);
        Assert.True(result.Timings.OcrElapsed >= TimeSpan.Zero);
        Assert.True(result.Timings.TranslationElapsed >= TimeSpan.Zero);
        Assert.True(result.Timings.MappingElapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCallTranslatorWhenOcrFindsNoText()
    {
        var ocr = new FakeOcrService(new OcrResult(
            Array.Empty<OcrTextBlock>(), "zh-Hans-CN", false, 0, TimeSpan.Zero));
        var coordinator = new ScreenshotTranslationCoordinator(ocr);
        var called = false;

        var result = await coordinator.ExecuteAsync(
            ValidImage(),
            (_, _) =>
            {
                called = true;
                return Task.FromResult<IReadOnlyList<TranslatedTextUnit>>(Array.Empty<TranslatedTextUnit>());
            });

        Assert.Equal(ScreenshotTranslationPipelineStatus.NoText, result.Status);
        Assert.False(called);
        Assert.True(result.Mapping.Accepted);
        Assert.Equal(0, result.Timings.OcrBlockCount);
        Assert.Equal(0, result.Timings.TranslationUnitCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsMappingRejectionAndPreservesUnits()
    {
        var ocr = new FakeOcrService(new OcrResult(
            new[] { new OcrTextBlock("b0001", "text", new OcrBounds(0, 0, 20, 10)) },
            "zh-Hans-CN", false, 0, TimeSpan.Zero));
        var coordinator = new ScreenshotTranslationCoordinator(ocr);

        var result = await coordinator.ExecuteAsync(
            ValidImage(),
            (_, _) => Task.FromResult<IReadOnlyList<TranslatedTextUnit>>(
                new[] { new TranslatedTextUnit("wrong-id", "错位") }));

        Assert.Equal(ScreenshotTranslationPipelineStatus.TranslationMappingRejected, result.Status);
        Assert.Equal("unexpected_id", result.Mapping.Reason);
        Assert.Single(result.Units);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationBeforeTranslation()
    {
        var ocr = new FakeOcrService(new OcrResult(
            new[] { new OcrTextBlock("b0001", "text", new OcrBounds(0, 0, 20, 10)) },
            "zh-Hans-CN", false, 0, TimeSpan.Zero));
        var coordinator = new ScreenshotTranslationCoordinator(ocr);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var translatorCalled = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(
            ValidImage(),
            (_, _) =>
            {
                translatorCalled = true;
                return Task.FromResult<IReadOnlyList<TranslatedTextUnit>>(Array.Empty<TranslatedTextUnit>());
            },
            cancellationToken: cancellation.Token));

        Assert.False(translatorCalled);
    }

    private static OcrImage ValidImage() => new(100, 100, 400, new byte[40_000]);

    private sealed class FakeOcrService : IOcrService
    {
        private readonly OcrResult _result;

        public FakeOcrService(OcrResult result) => _result = result;

        public OcrCapability Probe() => OcrCapability.Available(new[] { _result.UsedLanguageTag }, 10_000);

        public Task<OcrResult> RecognizeAsync(
            OcrImage image,
            OcrRecognitionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            image.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }
}
