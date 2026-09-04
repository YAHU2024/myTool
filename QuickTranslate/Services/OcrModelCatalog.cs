using System.IO;

namespace QuickTranslate.Services;

public sealed record OcrModelArtifact(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    Uri DownloadUri);

public sealed record OcrModelDescriptor(
    string Id,
    string Version,
    string DisplayName,
    string Description,
    string License,
    long TotalSizeBytes,
    IReadOnlyList<OcrModelArtifact> Artifacts)
{
    public string DetModelRelativePath => "det/inference.onnx";
    public string RecModelRelativePath => "rec/inference.onnx";
    public string RecKeysRelativePath => "rec/ppocrv6_dict.txt";
}

/// <summary>
/// Immutable official PaddlePaddle Hugging Face catalog. Every URL is pinned to
/// a reviewed revision and every delivered byte is verified before installation.
/// </summary>
public static class OcrModelCatalog
{
    public const string DefaultModelId = "ppocrv6-small-cpu";

    public static IReadOnlyList<OcrModelDescriptor> All { get; } = new[]
    {
        Create(
            "ppocrv6-small-cpu",
            "1.0.0",
            "PP-OCRv6 Small（质量优先，实验性）",
            "复杂场景质量候选；CPU 速度较慢，尚未达到生产性能门禁。",
            "PaddlePaddle/PP-OCRv6_small_det_onnx", "28fe5895c24fd108c19eb3e8479f4ab385fbfc62",
            new("det/inference.onnx", 9_880_512, "D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E", null!),
            "PaddlePaddle/PP-OCRv6_small_rec_onnx", "b8f84f0b80c529de40b4fbb3544b84fa7233a513",
            new[]
            {
                new OcrModelArtifact("rec/inference.onnx", 21_159_378, "5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634", null!),
                new OcrModelArtifact("rec/ppocrv6_dict.txt", 93_655, "769E7FA79BB297B5F18D8DBD149E364A45BC61F2B3F574E5EA836F0B261C23A6", null!),
                new OcrModelArtifact("rec/inference.yml", 150_579, "AB078671BB49F06228EADCCD34F1BB501E157F7A047095FFB943BA81512C77D1", null!)
            }),
        Create(
            "ppocrv6-tiny-cpu",
            "1.0.0",
            "PP-OCRv6 Tiny（速度优先，实验性）",
            "下载体积和 CPU 延迟较低，但复杂图片识别质量低于 Small。",
            "PaddlePaddle/PP-OCRv6_tiny_det_onnx", "2ba1506c0380b8f0b03dd142459aac66d4421f6c",
            new("det/inference.onnx", 1_780_590, "193BAB7A04FCA699A6C82E6ABB5B81BDB28177F0ABD4062552B04908DAFB19F8", null!),
            "PaddlePaddle/PP-OCRv6_tiny_rec_onnx", "2612ab37152ae0a677521bae4e1e3d4fb4cf7c30",
            new[]
            {
                new OcrModelArtifact("rec/inference.onnx", 4_462_639, "9EF676D6ED3C88256A2D92C640C44F25B0C40947E111B14B8BE8F594091563E6", null!),
                new OcrModelArtifact("rec/ppocrv6_dict.txt", 34_060, "2AF150BAB777D86FA1CE821F6A37EABEA90F1BB99AB9C41F031262E892A747BC", null!),
                new OcrModelArtifact("rec/inference.yml", 55_571, "66170210BAD538E83FFF3C4A3867E547D6BF20B50D64B20347C4B913F3034EA1", null!)
            })
    };

    public static OcrModelDescriptor? Find(string? id) => All.FirstOrDefault(
        model => string.Equals(model.Id, id, StringComparison.Ordinal));

    private static OcrModelDescriptor Create(
        string id,
        string version,
        string displayName,
        string description,
        string detRepository,
        string detRevision,
        OcrModelArtifact detArtifact,
        string recRepository,
        string recRevision,
        IReadOnlyList<OcrModelArtifact> recArtifacts)
    {
        var artifacts = new List<OcrModelArtifact>
        {
            WithUri(detArtifact, detRepository, detRevision)
        };
        artifacts.AddRange(recArtifacts.Select(artifact => WithUri(artifact, recRepository, recRevision)));
        return new(
            id,
            version,
            displayName,
            description,
            "Apache-2.0（官方模型卡声明）",
            artifacts.Sum(static artifact => artifact.SizeBytes),
            artifacts);
    }

    private static OcrModelArtifact WithUri(
        OcrModelArtifact artifact,
        string repository,
        string revision)
    {
        var sourceName = Path.GetFileName(artifact.RelativePath);
        return artifact with
        {
            DownloadUri = new Uri(
                $"https://huggingface.co/{repository}/resolve/{revision}/{sourceName}?download=true")
        };
    }
}
