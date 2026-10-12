using QuickTranslate.Core;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TranslationDirectionResolverTests
{
    [Fact]
    public void Resolve_EnglishDominantMarkdownWithEmbeddedChinese_TargetChineseDoesNotFallback()
    {
        const string source = """
            # Android setup

            This document explains how to build and install the Android application on a local device.
            Keep the model path and command unchanged while translating every explanatory paragraph.
            The settings screen contains a button named 查看开源许可 for bundled license texts.
            """;

        var decision = TranslationDirectionResolver.Resolve(
            source,
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Translation);

        Assert.Equal(LanguageRelation.Different, decision.Relation);
        Assert.Equal(LanguageDetectionConfidence.High, decision.Confidence);
        Assert.Equal(SourceLanguageFamily.Latin, decision.SourceLanguageFamily);
        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_ChineseSourceTargetChinese_UsesFallback()
    {
        const string source = "这是一段完整的中文正文，用于确认高置信度同语言判断能够选择备选语言。";

        var decision = TranslationDirectionResolver.Resolve(
            source,
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Translation);

        Assert.Equal(LanguageRelation.Same, decision.Relation);
        Assert.Equal("English", decision.EffectiveTargetLanguage);
        Assert.True(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_AutoDetectionDisabled_NeverUsesFallback()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文。",
            "简体中文",
            "English",
            autoDetectLanguage: false,
            ContentType.Translation);

        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.AutoDetectionDisabled, decision.Reason);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_LatinSourceAndLatinTarget_RemainsUnknownAndUsesRequestedTarget()
    {
        const string french = "Ce document explique comment installer et configurer correctement cette application locale.";

        var decision = TranslationDirectionResolver.Resolve(
            french,
            "English",
            "简体中文",
            autoDetectLanguage: true,
            ContentType.Translation);

        Assert.Equal(LanguageRelation.Unknown, decision.Relation);
        Assert.Equal(SourceLanguageFamily.Latin, decision.SourceLanguageFamily);
        Assert.Equal("English", decision.EffectiveTargetLanguage);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_NonTranslationMode_DoesNotUseFallback()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文。",
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Code);

        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.ModeDoesNotUseFallback, decision.Reason);
    }

    [Fact]
    public void Resolve_ManualFallbackOverridesUnknownMixedText()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "fix translation 中文内容 and command examples",
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Translation,
            TranslationDirectionPreference.FallbackTarget);

        Assert.Equal("English", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.UserSelectedFallback, decision.Reason);
        Assert.True(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_ManualRequestedTargetOverridesAutoDetectedFallback()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文，用于触发自动备选语言。",
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Translation,
            TranslationDirectionPreference.RequestedTarget);

        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.UserSelectedTarget, decision.Reason);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_ScreenshotFixedTarget_NeverUsesFallbackForSameLanguageSource()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文，用于截图翻译统一输出语言。",
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Translation,
            TranslationDirectionPreference.FixedRequestedTarget);

        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.ScreenshotFixedTarget, decision.Reason);
        Assert.False(decision.FallbackUsed);
    }

    [Fact]
    public void Resolve_ManualFallbackWorksWhenAutoDetectionIsDisabled()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文。",
            "简体中文",
            "English",
            autoDetectLanguage: false,
            ContentType.Translation,
            TranslationDirectionPreference.FallbackTarget);

        Assert.Equal("English", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.UserSelectedFallback, decision.Reason);
    }

    [Fact]
    public void Resolve_NonTranslationModeIgnoresManualFallback()
    {
        var decision = TranslationDirectionResolver.Resolve(
            "这是一段明确的中文正文。",
            "简体中文",
            "English",
            autoDetectLanguage: true,
            ContentType.Code,
            TranslationDirectionPreference.FallbackTarget);

        Assert.Equal("简体中文", decision.EffectiveTargetLanguage);
        Assert.Equal(TranslationDirectionReason.ModeDoesNotUseFallback, decision.Reason);
    }
}
