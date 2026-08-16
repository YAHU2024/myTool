namespace QuickTranslate.Core;

using QuickTranslate.Helpers;

internal sealed record TranslationRouteDecision(
    ContentType InitialMode,
    DetectionResult? ContentDecision);

internal static class TranslationRouteResolver
{
    public static TranslationRouteDecision Resolve(string text, bool smartContentType)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!smartContentType)
            return new(ContentType.Translation, null);

        var detection = ContentTypeDetector.DetectDetailed(text);
        Logger.Debug("ContentTypeDetector", ContentTypeDetector.FormatDiagnostic(detection));
        var initialMode = detection.Confidence == DetectionConfidence.High
            ? detection.ContentType
            : ContentType.Translation;
        return new(initialMode, detection);
    }
}
