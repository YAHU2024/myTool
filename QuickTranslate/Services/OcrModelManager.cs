using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslate.Helpers;

namespace QuickTranslate.Services;

public enum OcrModelInstallState
{
    NotInstalled,
    Installed,
    Corrupted
}

public sealed record OcrModelInstallStatus(
    OcrModelDescriptor Model,
    OcrModelInstallState State,
    string? InstallDirectory);

public sealed record OcrModelDownloadProgress(
    string ModelId,
    int CompletedFiles,
    int TotalFiles,
    long DownloadedBytes,
    long TotalBytes,
    string Stage)
{
    public double? Percentage => TotalBytes > 0
        ? Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100)
        : null;
}

/// <summary>
/// Manages user-selected OCR weights under AppData. Downloads are resumable,
/// verified against the immutable catalog and atomically promoted only after
/// every artifact passes size and SHA-256 checks.
/// </summary>
public sealed class OcrModelManager : IDisposable
{
    private const string InstallManifestName = "install-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _usageLock = new();
    private readonly Dictionary<string, int> _modelUsage = new(StringComparer.Ordinal);
    private string? _activeModelId;

    public OcrModelManager(string? rootDirectory = null)
        : this(
            rootDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickTranslate",
                "ocr-models"),
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            ownsClient: true)
    {
    }

    internal OcrModelManager(string rootDirectory, HttpMessageHandler handler)
        : this(rootDirectory, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, ownsClient: true)
    {
    }

    private OcrModelManager(string rootDirectory, HttpClient httpClient, bool ownsClient)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("OCR 模型目录不能为空。", nameof(rootDirectory));
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    public string RootDirectory => _rootDirectory;

    public string? ActiveModelId => Volatile.Read(ref _activeModelId);

    public void SetActiveModel(string? modelId) =>
        Volatile.Write(ref _activeModelId, string.IsNullOrWhiteSpace(modelId) ? null : modelId);

    public bool HasPartialDownload(OcrModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Directory.Exists(GetStagingDirectory(model));
    }

    public bool IsModelInUse(string modelId)
    {
        lock (_usageLock)
            return _modelUsage.TryGetValue(modelId, out var count) && count > 0;
    }

    public IDisposable AcquireUsage(OcrModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_usageLock)
        {
            _modelUsage.TryGetValue(model.Id, out var count);
            _modelUsage[model.Id] = checked(count + 1);
        }
        return new ModelUsageLease(this, model.Id);
    }

    public OcrModelInstallStatus GetStatus(OcrModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var directory = GetInstallDirectory(model);
        if (!Directory.Exists(directory))
            return new(model, OcrModelInstallState.NotInstalled, null);
        return new(
            model,
            HasExpectedLayout(model, directory)
                ? OcrModelInstallState.Installed
                : OcrModelInstallState.Corrupted,
            directory);
    }

    public async Task<string> VerifyInstalledAsync(
        OcrModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        cancellationToken = linkedCts.Token;
        var directory = GetInstallDirectory(model);
        if (!Directory.Exists(directory))
            throw new InvalidDataException("OCR 模型尚未安装。");

        foreach (var artifact in model.Artifacts)
        {
            var path = ResolveContainedPath(directory, artifact.RelativePath);
            await VerifyFileAsync(path, artifact, cancellationToken).ConfigureAwait(false);
        }

        return directory;
    }

    public async Task<string> InstallAsync(
        OcrModelDescriptor model,
        IProgress<OcrModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        cancellationToken = linkedCts.Token;
        var finalDirectory = GetInstallDirectory(model);
        if (Directory.Exists(finalDirectory))
        {
            if (IsModelInUse(model.Id))
                throw new InvalidOperationException("正在使用的 OCR 模型不能修复或覆盖安装。");
            try
            {
                return await VerifyInstalledAsync(model, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // The user explicitly requested download/repair. Only remove
                // this catalog model's versioned managed directory after a
                // complete integrity check has proved it unusable.
                lock (_usageLock)
                {
                    if (_modelUsage.TryGetValue(model.Id, out var count) && count > 0)
                        throw new InvalidOperationException("正在使用的 OCR 模型不能修复或覆盖安装。");
                    Directory.Delete(finalDirectory, recursive: true);
                }
            }
        }

        Directory.CreateDirectory(_rootDirectory);
        var stagingDirectory = GetStagingDirectory(model);
        Directory.CreateDirectory(stagingDirectory);

        long completedBytes = 0;
        var completedFiles = 0;
        foreach (var artifact in model.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedPath = ResolveContainedPath(stagingDirectory, artifact.RelativePath);
            if (File.Exists(completedPath))
            {
                try
                {
                    await VerifyFileAsync(completedPath, artifact, cancellationToken).ConfigureAwait(false);
                    completedBytes += artifact.SizeBytes;
                    completedFiles++;
                    Report("downloading");
                    continue;
                }
                catch (InvalidDataException)
                {
                    File.Delete(completedPath);
                }
            }

            if (artifact.Kind == OcrModelArtifactKind.DerivedCharacterDictionary)
            {
                // The PP-OCRv6 ONNX repositories do not publish the dictionary
                // as a separate file. Generate it from the already verified
                // inference.yml instead of requesting a non-existent URL.
                var generatedPartialPath = completedPath + ".part";
                TryDelete(generatedPartialPath);
                try
                {
                    var sourceArtifact = model.Artifacts.FirstOrDefault(static candidate =>
                        candidate.Kind == OcrModelArtifactKind.RemoteDownload &&
                        string.Equals(candidate.RelativePath, "rec/inference.yml", StringComparison.Ordinal));
                    if (sourceArtifact is null)
                        throw new InvalidDataException("OCR 模型字符表来源未登记。");
                    await VerifyFileAsync(
                        ResolveContainedPath(stagingDirectory, sourceArtifact.RelativePath),
                        sourceArtifact,
                        cancellationToken).ConfigureAwait(false);
                    await GenerateCharacterDictionaryAsync(
                        stagingDirectory,
                        artifact,
                        generatedPartialPath,
                        cancellationToken).ConfigureAwait(false);
                    await VerifyFileAsync(generatedPartialPath, artifact, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    TryDelete(generatedPartialPath);
                    throw;
                }
                File.Move(generatedPartialPath, completedPath, overwrite: true);
                completedBytes += artifact.SizeBytes;
                completedFiles++;
                Report("verifying");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(completedPath)!);
            var partialPath = completedPath + ".part";
            var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existingBytes > artifact.SizeBytes)
            {
                File.Delete(partialPath);
                existingBytes = 0;
            }

            // A cancelled download may leave a complete-sized .part file. Verify it
            // locally before attempting a Range request, because asking for bytes
            // after EOF commonly returns 416 and prevents recovery from a bad part.
            if (existingBytes == artifact.SizeBytes)
            {
                try
                {
                    await VerifyFileAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false);
                    File.Move(partialPath, completedPath, overwrite: true);
                    completedBytes += artifact.SizeBytes;
                    completedFiles++;
                    Report("verifying");
                    continue;
                }
                catch (InvalidDataException)
                {
                    File.Delete(partialPath);
                    existingBytes = 0;
                }
            }

            try
            {
                await DownloadArtifactAsync(
                    model,
                    artifact,
                    partialPath,
                    existingBytes,
                    completedBytes,
                    completedFiles,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                await VerifyFileAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // A size/hash failure is not resumable. Keep .part only for
                // cancellation, transport failures, and process interruption.
                TryDelete(partialPath);
                throw;
            }
            File.Move(partialPath, completedPath, overwrite: true);
            completedBytes += artifact.SizeBytes;
            completedFiles++;
            Report("verifying");
        }

        var manifest = new InstallManifest(
            "quicktranslate.ocr-model-install.v1",
            model.Id,
            model.Version,
            DateTimeOffset.UtcNow,
            model.Artifacts.Select(static artifact => new InstallArtifact(
                artifact.RelativePath,
                artifact.SizeBytes,
                artifact.Sha256)).ToArray());
        var manifestPath = Path.Combine(stagingDirectory, InstallManifestName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        Directory.Move(stagingDirectory, finalDirectory);
        progress?.Report(new(model.Id, model.Artifacts.Count, model.Artifacts.Count,
            model.TotalSizeBytes, model.TotalSizeBytes, "installed"));
        Logger.Info("Screenshot", "screenshot.ocr_model_installed", new
        {
            model_id = model.Id,
            model_version = model.Version,
            total_bytes = model.TotalSizeBytes,
            file_count = model.Artifacts.Count
        });
        return finalDirectory;

        void Report(string stage) => progress?.Report(new(
            model.Id,
            completedFiles,
            model.Artifacts.Count,
            completedBytes,
            model.TotalSizeBytes,
            stage));
    }

    public void Delete(OcrModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_usageLock)
        {
            if (_modelUsage.TryGetValue(model.Id, out var count) && count > 0)
                throw new InvalidOperationException("正在使用的 OCR 模型不能删除。");
            var directory = GetInstallDirectory(model);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            var stagingDirectory = GetStagingDirectory(model);
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
        Logger.Info("Screenshot", "screenshot.ocr_model_deleted", new
        {
            model_id = model.Id,
            model_version = model.Version
        });
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private async Task DownloadArtifactAsync(
        OcrModelDescriptor model,
        OcrModelArtifact artifact,
        string partialPath,
        long existingBytes,
        long completedBytes,
        int completedFiles,
        IProgress<OcrModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (artifact.Kind != OcrModelArtifactKind.RemoteDownload || artifact.DownloadUri is null)
            throw new InvalidDataException("OCR 模型下载清单包含无效远程文件。");
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        if (existingBytes > 0)
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (existingBytes > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new InvalidDataException("OCR 模型下载续传范围已失效。");
        response.EnsureSuccessStatusCode();

        var append = false;
        if (existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != existingBytes)
                throw new InvalidDataException("OCR 模型下载续传位置与服务器响应不一致。");
            append = true;
        }
        if (!append)
            existingBytes = 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        var currentBytes = existingBytes;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            currentBytes += read;
            if (currentBytes > artifact.SizeBytes)
                throw new InvalidDataException("OCR 模型下载大小超过清单声明。");
            progress?.Report(new(
                model.Id,
                completedFiles,
                model.Artifacts.Count,
                completedBytes + currentBytes,
                model.TotalSizeBytes,
                "downloading"));
        }
    }

    private static async Task GenerateCharacterDictionaryAsync(
        string stagingDirectory,
        OcrModelArtifact artifact,
        string partialPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolveContainedPath(
            stagingDirectory,
            "rec/inference.yml");
        if (!File.Exists(sourcePath))
            throw new InvalidDataException("OCR 模型字符表来源文件缺失。");

        var yaml = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var lines = yaml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var markerIndex = Array.FindIndex(lines, static line =>
            string.Equals(line.TrimEnd('\r'), "  character_dict:", StringComparison.Ordinal));
        if (markerIndex < 0)
            throw new InvalidDataException("OCR 模型字符表配置缺失。");

        var characters = new List<string>();
        for (var index = markerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.StartsWith("  - ", StringComparison.Ordinal))
                break;
            characters.Add(ParseYamlCharacter(line[4..]));
        }

        if (characters.Count == 0)
            throw new InvalidDataException("OCR 模型字符表为空。");

        // Keep the official dictionary's UTF-8 without BOM and CRLF format.
        var dictionary = string.Join("\r\n", characters) + "\r\n";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(dictionary);
        if (bytes.Length != artifact.SizeBytes)
            throw new InvalidDataException("OCR 模型派生字符表大小与清单不一致。");

        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static string ParseYamlCharacter(string scalar)
    {
        if (scalar.Length >= 2 && scalar[0] == '\'' && scalar[^1] == '\'')
            return scalar[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (scalar.Contains('#', StringComparison.Ordinal))
            throw new InvalidDataException("OCR 模型字符表包含未支持的 YAML 标量。");
        return scalar;
    }

    private static async Task VerifyFileAsync(
        string path,
        OcrModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes)
            throw new InvalidDataException("OCR 模型文件大小与清单不一致。");
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(hash), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR 模型文件 SHA-256 校验失败。");
    }

    private bool HasExpectedLayout(OcrModelDescriptor model, string directory)
    {
        var manifestPath = Path.Combine(directory, InstallManifestName);
        if (!File.Exists(manifestPath))
            return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath), JsonOptions);
            return manifest is not null &&
                   string.Equals(manifest.Schema, "quicktranslate.ocr-model-install.v1", StringComparison.Ordinal) &&
                   string.Equals(manifest.ModelId, model.Id, StringComparison.Ordinal) &&
                   string.Equals(manifest.Version, model.Version, StringComparison.Ordinal) &&
                   model.Artifacts.All(artifact =>
                   {
                       var path = ResolveContainedPath(directory, artifact.RelativePath);
                       return File.Exists(path) && new FileInfo(path).Length == artifact.SizeBytes;
                   });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string GetInstallDirectory(OcrModelDescriptor model) =>
        Path.Combine(_rootDirectory, model.Id, model.Version);

    private string GetStagingDirectory(OcrModelDescriptor model) =>
        Path.Combine(_rootDirectory, ".downloads", model.Id, model.Version);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ReleaseUsage(string modelId)
    {
        lock (_usageLock)
        {
            if (!_modelUsage.TryGetValue(modelId, out var count))
                return;
            if (count <= 1)
                _modelUsage.Remove(modelId);
            else
                _modelUsage[modelId] = count - 1;
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR 模型清单包含越界路径。");
        return path;
    }

    private sealed record InstallManifest(
        string Schema,
        string ModelId,
        string Version,
        DateTimeOffset InstalledAt,
        IReadOnlyList<InstallArtifact> Files);

    private sealed record InstallArtifact(string Path, long SizeBytes, string Sha256);

    private sealed class ModelUsageLease(OcrModelManager owner, string modelId) : IDisposable
    {
        private OcrModelManager? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseUsage(modelId);
    }
}
