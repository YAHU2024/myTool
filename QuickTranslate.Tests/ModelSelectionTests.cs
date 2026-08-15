using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class ModelSelectionTests
{
    [Fact]
    public void Catalog_UsesAliasAndFallsBackToModelAndProvider()
    {
        var aliased = ModelProfileCatalog.Create(new SavedConfig
        {
            Id = "provider:a",
            Alias = "长文专用",
            ModelName = "Qwen/Qwen3-8B",
            ApiBaseUrl = "https://api.siliconflow.cn/v1",
            ApiKey = "key-a"
        });
        var fallback = ModelProfileCatalog.Create(new SavedConfig
        {
            Id = "provider:b",
            ModelName = "glm-4.7-flash",
            ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ApiKey = "key-b"
        });

        Assert.Equal("长文专用", aliased.DisplayName);
        Assert.Equal("长文专用", aliased.SelectorDisplayName);
        Assert.Equal("glm-4.7-flash · 智谱 GLM", fallback.DisplayName);
        Assert.Equal("glm-4.7-flash", fallback.SelectorDisplayName);
        Assert.Equal("glm-4.7-flash · 智谱 GLM", fallback.MenuDetail);
    }

    [Theory]
    [InlineData("Qwen/Qwen3-8B", "Qwen3-8B")]
    [InlineData("tencent/Hunyuan-MT-7B", "Hunyuan-MT-7B")]
    [InlineData("deepseek-v4-flash", "deepseek-v4-flash")]
    [InlineData("trailing/", "trailing/")]
    [InlineData("  model-name  ", "model-name")]
    public void CompactModelName_RemovesOnlyOrganizationPrefix(string modelName, string expected)
    {
        Assert.Equal(expected, ModelProfileCatalog.CompactModelName(modelName));
    }

    [Fact]
    public void Catalog_NormalizesAliasWithoutChangingConfigurationIdentity()
    {
        var config = new SavedConfig
        {
            Id = "provider:stable",
            Alias = "  work\r\nmodel\u0001  ",
            ModelName = "model",
            ApiBaseUrl = "https://example.com/v1",
            ApiKey = "key"
        };

        var profile = ModelProfileCatalog.Create(config);

        Assert.Equal("provider:stable", profile.Id);
        Assert.Equal("workmodel", profile.Alias);
        Assert.Equal("model", profile.ModelName);
        Assert.Equal("key", profile.ApiKey);
    }

    [Fact]
    public void Catalog_AddsTemporaryCurrentProfileOnlyWhenNotSaved()
    {
        var request = CreateRequest("current-model", "https://current.example/v1", "current-key");

        var profiles = ModelProfileCatalog.Build([], request, "current:session");

        var current = Assert.Single(profiles);
        Assert.True(current.IsTemporary);
        Assert.Equal("current:session", current.Id);
        Assert.Equal("current-model · current.example", current.DisplayName);
    }

    [Fact]
    public void Coordinator_SameModelIsNoOpAndDifferentModelCreatesSnapshot()
    {
        var coordinator = new ModelSelectionCoordinator();
        var sessionId = Guid.NewGuid();
        var request = CreateRequest("model-a", "https://a.example/v1", "key-a");
        var first = new ModelProfile(
            "provider:a", "A", "model-a", "a.example", request.ApiBaseUrl, request.ApiKey);
        coordinator.BeginSession(sessionId, ContentType.Translation, first, request);

        var noOp = coordinator.Select(sessionId, ContentType.Translation, first, requestIsRunning: true);
        var second = new ModelProfile(
            "provider:b", "B", "model-b", "b.example", "https://b.example/v1", "key-b");
        var switched = coordinator.Select(sessionId, ContentType.Translation, second, requestIsRunning: true);

        Assert.Equal(ModelSelectionIntent.NoOp, noOp.Intent);
        Assert.Equal(ModelSelectionIntent.CancelAndStart, switched.Intent);
        Assert.NotNull(switched.Request);
        Assert.Equal("model-b", switched.Request!.ModelName);
        Assert.Equal("https://b.example/v1", switched.Request.ApiBaseUrl);
        Assert.Equal("key-b", switched.Request.ApiKey);
        Assert.Equal(request.Text, switched.Request.Text);
        Assert.Equal(request.TargetLanguage, switched.Request.TargetLanguage);
        Assert.Equal(request.SystemPrompt, switched.Request.SystemPrompt);
        Assert.Equal("model-a", request.ModelName);
    }

    [Fact]
    public void Coordinator_RejectsWrongSessionModeAndIncompleteProfile()
    {
        var coordinator = new ModelSelectionCoordinator();
        var sessionId = Guid.NewGuid();
        var request = CreateRequest("model-a", "https://a.example/v1", "key-a");
        var first = new ModelProfile(
            "provider:a", string.Empty, "model-a", "a.example", request.ApiBaseUrl, request.ApiKey);
        coordinator.BeginSession(sessionId, ContentType.Translation, first, request);
        var incomplete = new ModelProfile(
            "provider:bad", string.Empty, "model-b", "b.example", "https://b.example/v1", string.Empty);

        Assert.Equal(
            ModelSelectionIntent.OpenSettings,
            coordinator.Select(Guid.NewGuid(), ContentType.Translation, first, false).Intent);
        Assert.Equal(
            ModelSelectionIntent.OpenSettings,
            coordinator.Select(sessionId, ContentType.Code, first, false).Intent);
        Assert.Equal(
            ModelSelectionIntent.OpenSettings,
            coordinator.Select(sessionId, ContentType.Translation, incomplete, false).Intent);
    }

    [Fact]
    public void Coordinator_SettingsRefreshUpdatesAliasWithoutChangingRequestTemplate()
    {
        var coordinator = new ModelSelectionCoordinator();
        var sessionId = Guid.NewGuid();
        var request = CreateRequest("model-a", "https://a.example/v1", "key-a");
        coordinator.BeginSession(
            sessionId,
            ContentType.Translation,
            ModelProfileCatalog.CreateCurrent(request, "current:session"),
            request);
        var saved = new ModelProfile(
            "provider:saved", "长文模型", "model-a", "a.example", request.ApiBaseUrl, request.ApiKey);

        coordinator.RefreshCurrentProfile(saved);
        Assert.Equal("provider:saved", coordinator.CurrentProfile!.Id);
        Assert.Equal("长文模型", coordinator.CurrentProfile.Alias);

        var switched = coordinator.Select(
            sessionId,
            ContentType.Translation,
            new ModelProfile(
                "provider:b", "B", "model-b", "b.example", "https://b.example/v1", "key-b"),
            requestIsRunning: false);

        Assert.Equal(request.SystemPrompt, switched.Request!.SystemPrompt);
    }

    private static TranslationRequest CreateRequest(string model, string apiBaseUrl, string apiKey) => new(
        TranslationRequestKind.Translation,
        "source text",
        "简体中文",
        ContentType.Translation,
        apiBaseUrl,
        apiKey,
        model,
        "translate prompt",
        FallbackUsed: false);
}
