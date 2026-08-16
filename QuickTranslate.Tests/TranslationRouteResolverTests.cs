using QuickTranslate.Core;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class TranslationRouteResolverTests
{
    [Fact]
    public void Resolve_SmartContentDisabled_DoesNotRunClassifier()
    {
        var decision = TranslationRouteResolver.Resolve("git status", smartContentType: false);

        Assert.Equal(ContentType.Translation, decision.InitialMode);
        Assert.Null(decision.ContentDecision);
    }

    [Fact]
    public void Resolve_HighConfidenceCommand_UsesCodeMode()
    {
        var decision = TranslationRouteResolver.Resolve("git status", smartContentType: true);

        Assert.Equal(ContentType.Code, decision.InitialMode);
        Assert.Equal(DetectionConfidence.High, decision.ContentDecision!.Confidence);
        Assert.Equal(DetectedContentKind.Command, decision.ContentDecision.Kind);
    }

    [Fact]
    public void Resolve_LowConfidenceCodeSuggestion_RemainsTranslation()
    {
        var decision = TranslationRouteResolver.Resolve("$ unknown-action", smartContentType: true);

        Assert.Equal(ContentType.Translation, decision.InitialMode);
        Assert.Equal(ContentType.Code, decision.ContentDecision!.ContentType);
        Assert.Equal(DetectionConfidence.Low, decision.ContentDecision.Confidence);
    }

    [Fact]
    public void Resolve_TechnicalMarkdownWithCodeFence_RemainsTranslation()
    {
        const string source = """
            # Local setup

            This document explains how to prepare the application for local Android development.
            Install the required SDK and keep the following command unchanged when translating this guide.

            ```powershell
            .\gradlew.bat assembleDebug
            ```
            """;

        var decision = TranslationRouteResolver.Resolve(source, smartContentType: true);

        Assert.Equal(ContentType.Translation, decision.InitialMode);
        Assert.Equal(DetectedContentKind.TechnicalDocument, decision.ContentDecision!.Kind);
        Assert.Contains("markdown-document", decision.ContentDecision.MatchedFeatures);
    }
}
