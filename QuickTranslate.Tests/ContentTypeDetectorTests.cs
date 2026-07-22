using System.Diagnostics;
using QuickTranslate.Core;
using Xunit;
using Xunit.Abstractions;

namespace QuickTranslate.Tests;

public sealed class ContentTypeDetectorTests
{
    private readonly ITestOutputHelper _output;

    public ContentTypeDetectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> CodeSamples => new()
    {
        "$ git status",
        "Get-ChildItem -Force",
        "SELECT id, name FROM users WHERE active = 1;",
        "```csharp\nvar answer = 42;\n```",
        "cat file.txt",
        "ls -la",
        "cat /etc/hosts | grep localhost",
        "echo hello > output.txt"
    };

    public static TheoryData<string> ConfigurationSamples => new()
    {
        "name: quick-translate\nenabled: true",
        "services:\n  - api\n  - worker",
        "[database]\nhost = \"localhost\"",
        "[application]\nenabled = true",
        "<?xml version=\"1.0\"?><settings><enabled>true</enabled></settings>",
        "<settings><enabled /></settings>",
        "API_URL=https://example.test\nAPI_TOKEN=secret",
        "API_TOKEN=\"${SECRET_TOKEN}\""
    };

    public static TheoryData<string> TermSamples => new()
    {
        "Kubernetes",
        "React Hooks",
        "Kubernetes 集群",
        "React Hooks 用法",
        "EF Core 配置",
        ".NET 框架",
        "Kubernetes\n是一个容器编排平台",
        "OpenAI\n用于构建人工智能应用"
    };

    public static TheoryData<string> TranslationSamples => new()
    {
        "这是一段普通中文。",
        "This is an ordinary English sentence.",
        "Note: hello",
        "12:30",
        "Hello 世界",
        "Please check 配置",
        "<not a tag",
        "[section]",
        "cat",
        "find",
        "make",
        "go",
        "Hello\n这是普通问候语",
        "Introduction\n这是文章的正文内容",
        "ratio: 3",
        "x < y"
    };

    public static TheoryData<string> ConfigurationLookalikes => new()
    {
        "Note: hello",
        "theme = dark",
        "[section]",
        "<not-a-complete-tag>",
        "PORT=8080"
    };

    [Theory]
    [MemberData(nameof(CodeSamples))]
    public void Detect_ReturnsCode_ForCodeAndCommandSamples(string input)
    {
        Assert.Equal(ContentType.Code, ContentTypeDetector.Detect(input));
    }

    [Theory]
    [MemberData(nameof(ConfigurationSamples))]
    public void Detect_ReturnsCode_ForConfigurationSamples(string input)
    {
        Assert.Equal(ContentType.Code, ContentTypeDetector.Detect(input));
    }

    [Theory]
    [MemberData(nameof(TermSamples))]
    public void Detect_ReturnsTerm_ForTechnicalTerms(string input)
    {
        Assert.Equal(ContentType.Term, ContentTypeDetector.Detect(input));
    }

    [Theory]
    [MemberData(nameof(TranslationSamples))]
    public void Detect_ReturnsTranslation_ForNaturalLanguageAndAmbiguousInputs(string input)
    {
        Assert.Equal(ContentType.Translation, ContentTypeDetector.Detect(input));
    }

    [Theory]
    [MemberData(nameof(ConfigurationLookalikes))]
    public void Detect_ReturnsTranslation_ForConfigurationLookalikes(string input)
    {
        Assert.Equal(ContentType.Translation, ContentTypeDetector.Detect(input));
    }

    [Theory]
    [InlineData(10 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(1024 * 1024)]
    public void Detect_ReturnsCode_ForValidJsonAtRepresentativeSizes(int targetSize)
    {
        var json = CreateJsonObject(targetSize);

        Assert.Equal(ContentType.Code, ContentTypeDetector.Detect(json));
    }

    [Fact]
    public void Detect_DoesNotThrow_ForInvalidOrDeepJson()
    {
        var invalidJson = "{\"items\":[1,2,}";
        var deeplyNestedJson = new string('[', 2_000) + "0" + new string(']', 2_000);

        var invalidException = Record.Exception(() => ContentTypeDetector.Detect(invalidJson));
        var deepException = Record.Exception(() => ContentTypeDetector.Detect(deeplyNestedJson));

        Assert.Null(invalidException);
        Assert.Null(deepException);
    }

    [Fact]
    public void DetectDetailed_ReturnsExplainableMetadata()
    {
        const string input = "git status";

        var result = ContentTypeDetector.DetectDetailed(input);

        Assert.Equal(ContentType.Code, result.ContentType);
        Assert.Equal(DetectionConfidence.Low, result.Confidence);
        Assert.True(result.Score >= result.Threshold);
        Assert.True(result.Threshold > 0);
        Assert.NotEmpty(result.MatchedFeatures);
        Assert.Equal(input.Length, result.CharacterCount);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
    }

    [Theory]
    [InlineData("```csharp\nvar answer = 42;\n```")]
    [InlineData("{\"name\":\"quick-translate\"}")]
    [InlineData("SELECT id FROM users WHERE active = 1;")]
    public void DetectDetailed_ReturnsHighConfidence_ForDeterministicCode(string input)
    {
        var result = ContentTypeDetector.DetectDetailed(input);

        Assert.Equal(ContentType.Code, result.ContentType);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
    }

    [Fact]
    public void DetectDetailed_ReturnsLowConfidence_ForOrdinaryTermRule()
    {
        var result = ContentTypeDetector.DetectDetailed("Kubernetes");

        Assert.Equal(ContentType.Term, result.ContentType);
        Assert.Equal(DetectionConfidence.Low, result.Confidence);
    }

    [Fact]
    public void DetectDetailed_ReturnsHighConfidence_ForStructuredTermDefinition()
    {
        var result = ContentTypeDetector.DetectDetailed("Kubernetes\n是一个容器编排平台");

        Assert.Equal(ContentType.Term, result.ContentType);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
    }

    [Fact]
    public void FormatDiagnostic_DoesNotContainOriginalInput()
    {
        const string secretInput = "git status --token uniquely-sensitive-value";
        var result = ContentTypeDetector.DetectDetailed(secretInput);

        var diagnostic = ContentTypeDetector.FormatDiagnostic(result);

        Assert.Contains(result.ContentType.ToString(), diagnostic, StringComparison.Ordinal);
        Assert.Contains(result.Score.ToString(), diagnostic, StringComparison.Ordinal);
        Assert.Contains(result.Threshold.ToString(), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(secretInput, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("uniquely-sensitive-value", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(1024 * 1024)]
    [Trait("Category", "Performance")]
    public void Detect_JsonMedianDuration_IsAtMostTwentyMilliseconds(int targetSize)
    {
        var json = CreateJsonObject(targetSize);
        for (var i = 0; i < 3; i++)
        {
            ContentTypeDetector.Detect(json);
        }

        var durations = new double[10];
        for (var i = 0; i < durations.Length; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var contentType = ContentTypeDetector.Detect(json);
            stopwatch.Stop();

            Assert.Equal(ContentType.Code, contentType);
            durations[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(durations);
        var medianMilliseconds = (durations[4] + durations[5]) / 2;
        _output.WriteLine(
            "JSON size: {0:N0} characters; median: {1:F3} ms; samples: {2}",
            targetSize,
            medianMilliseconds,
            string.Join(", ", durations.Select(duration => $"{duration:F3} ms")));
        Assert.True(
            medianMilliseconds <= 20,
            $"Median detection time for {targetSize:N0} characters was {medianMilliseconds:F3} ms.");
    }

    private static string CreateJsonObject(int targetSize)
    {
        const string prefix = "{\"payload\":\"";
        const string suffix = "\"}";
        return prefix + new string('a', targetSize - prefix.Length - suffix.Length) + suffix;
    }
}
