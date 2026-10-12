using System.IO;

namespace QuickTranslate.Services;

public enum MangaModelInstallState { Missing, Invalid, Installed }

public sealed record MangaModelStatus(
    MangaModelInstallState State,
    string ModelName,
    string Directory,
    long TotalBytes,
    string? Reason);

public static class MangaModelStatusService
{
    public static MangaModelStatus Inspect(string modelName, string? directory, params string[] requiredFiles)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new(MangaModelInstallState.Missing, modelName, string.Empty, 0, "未配置模型目录");
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
            return new(MangaModelInstallState.Missing, modelName, full, 0, "模型目录不存在");
        var files = requiredFiles.Select(name => Path.Combine(full, name)).ToArray();
        if (files.Any(path => !File.Exists(path) || new FileInfo(path).Length == 0))
            return new(MangaModelInstallState.Invalid, modelName, full, files.Where(File.Exists).Sum(path => new FileInfo(path).Length), "模型文件缺失或为空");
        return new(MangaModelInstallState.Installed, modelName, full, files.Sum(path => new FileInfo(path).Length), null);
    }
}
