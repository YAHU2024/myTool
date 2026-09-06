using System.Text.Json.Serialization;
using System.IO;

namespace QuickTranslate.Services;

public sealed record MangaWorkerRequest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("image_path")] string ImagePath,
    [property: JsonPropertyName("source_language")] string SourceLanguage,
    [property: JsonPropertyName("stages")] IReadOnlyList<string> Stages,
    [property: JsonPropertyName("output_directory")] string? OutputDirectory,
    [property: JsonPropertyName("include_source_text")] bool IncludeSourceText = false,
    [property: JsonPropertyName("allow_model_download")] bool AllowModelDownload = false);

public static class MangaWorkerRequestFactory
{
    public static MangaWorkerRequest Create(
        string requestId,
        string imagePath,
        string sourceLanguage,
        bool inpaint,
        string? outputDirectory = null) {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("请求 ID 不能为空。", nameof(requestId));
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("图片路径不能为空。", nameof(imagePath));
        if (string.IsNullOrWhiteSpace(sourceLanguage)) throw new ArgumentException("源语言不能为空。", nameof(sourceLanguage));
        var stages = inpaint ? new[] { "detect", "ocr", "inpaint" } : new[] { "detect", "ocr" };
        return new("quicktranslate.manga-worker.v1", "translate", requestId, Path.GetFullPath(imagePath), sourceLanguage, stages, outputDirectory is null ? null : Path.GetFullPath(outputDirectory));
    }
}
