using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class OcrModelManagerTests
{
    [Fact]
    public async Task InstallAsync_VerifiesArtifactsAndPromotesAtomically()
    {
        var root = CreateTempDirectory();
        var det = Encoding.UTF8.GetBytes("det");
        var rec = Encoding.UTF8.GetBytes("rec");
        var model = CreateModel(det, rec);
        using var manager = new OcrModelManager(root, new MapHandler(new Dictionary<string, byte[]>
        {
            [model.Artifacts[0].DownloadUri!.ToString()] = det,
            [model.Artifacts[1].DownloadUri!.ToString()] = rec
        }));

        var installed = await manager.InstallAsync(model);

        Assert.Equal(Path.Combine(root, model.Id, model.Version), installed);
        Assert.Equal(OcrModelInstallState.Installed, manager.GetStatus(model).State);
        await manager.VerifyInstalledAsync(model);
        Assert.True(File.Exists(Path.Combine(installed, "det", "inference.onnx")));
    }

    [Fact]
    public async Task InstallAsync_GeneratesDictionaryFromVerifiedInferenceYaml()
    {
        var root = CreateTempDirectory();
        var det = Encoding.UTF8.GetBytes("det");
        var rec = Encoding.UTF8.GetBytes("rec");
        var yaml = Encoding.UTF8.GetBytes("  character_dict:\r\n  - A\r\n  - ''''\r\n");
        var dictionary = Encoding.UTF8.GetBytes("A\r\n'\r\n");
        var model = new OcrModelDescriptor(
            "generated-model",
            "1",
            "Generated",
            "Generated",
            "Apache-2.0",
            det.Length + rec.Length + yaml.Length + dictionary.Length,
            new[]
            {
                Artifact("det/inference.onnx", det, "https://example.invalid/det"),
                Artifact("rec/inference.onnx", rec, "https://example.invalid/rec"),
                Artifact("rec/inference.yml", yaml, "https://example.invalid/yaml"),
                new OcrModelArtifact(
                    "rec/ppocrv6_dict.txt",
                    dictionary.Length,
                    Convert.ToHexString(SHA256.HashData(dictionary)),
                    null,
                    OcrModelArtifactKind.DerivedCharacterDictionary)
            });
        using var manager = new OcrModelManager(root, new MapHandler(new Dictionary<string, byte[]>
        {
            [model.Artifacts[0].DownloadUri!.ToString()] = det,
            [model.Artifacts[1].DownloadUri!.ToString()] = rec,
            [model.Artifacts[2].DownloadUri!.ToString()] = yaml
        }));

        var installed = await manager.InstallAsync(model);

        Assert.Equal("A\r\n'\r\n", await File.ReadAllTextAsync(
            Path.Combine(installed, "rec", "ppocrv6_dict.txt")));
        await manager.VerifyInstalledAsync(model);
    }

    [Fact]
    public async Task InstallAsync_HashFailureDoesNotPromoteAndRemovesBadPart()
    {
        var root = CreateTempDirectory();
        var expected = Encoding.UTF8.GetBytes("expected");
        var model = CreateModel(expected, expected);
        using var manager = new OcrModelManager(root, new MapHandler(new Dictionary<string, byte[]>
        {
            [model.Artifacts[0].DownloadUri!.ToString()] = Encoding.UTF8.GetBytes("xxxxxxxx"),
            [model.Artifacts[1].DownloadUri!.ToString()] = expected
        }));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(model));

        Assert.False(Directory.Exists(Path.Combine(root, model.Id, model.Version)));
        Assert.False(File.Exists(Path.Combine(root, ".downloads", model.Id, model.Version, "det", "inference.onnx.part")));
    }

    [Fact]
    public async Task InstallAsync_ResumesFromValidatedContentRange()
    {
        var root = CreateTempDirectory();
        var det = Encoding.UTF8.GetBytes("det-model");
        var rec = Encoding.UTF8.GetBytes("rec-model");
        var model = CreateModel(det, rec);
        var partialPath = Path.Combine(root, ".downloads", model.Id, model.Version, "det", "inference.onnx.part");
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, det[..3]);
        var handler = new RangeHandler(model, det, rec);
        using var manager = new OcrModelManager(root, handler);

        await manager.InstallAsync(model);

        Assert.Equal(3, handler.DetRangeStart);
        await manager.VerifyInstalledAsync(model);
    }

    [Fact]
    public void Delete_RejectsModelWithActiveUsageLease()
    {
        var root = CreateTempDirectory();
        var bytes = Encoding.UTF8.GetBytes("model");
        var model = CreateModel(bytes, bytes);
        using var manager = new OcrModelManager(root, new MapHandler(new Dictionary<string, byte[]>()));
        var installDirectory = Path.Combine(root, model.Id, model.Version);
        Directory.CreateDirectory(installDirectory);
        using var usage = manager.AcquireUsage(model);

        Assert.Throws<InvalidOperationException>(() => manager.Delete(model));
        Assert.True(Directory.Exists(installDirectory));
    }

    [Fact]
    public void Delete_RemovesCancelledDownloadStagingDirectory()
    {
        var root = CreateTempDirectory();
        var bytes = Encoding.UTF8.GetBytes("model");
        var model = CreateModel(bytes, bytes);
        using var manager = new OcrModelManager(root, new MapHandler(new Dictionary<string, byte[]>()));
        var stagingDirectory = Path.Combine(root, ".downloads", model.Id, model.Version);
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "model.part"), "partial");

        Assert.True(manager.HasPartialDownload(model));
        manager.Delete(model);

        Assert.False(Directory.Exists(stagingDirectory));
        Assert.False(manager.HasPartialDownload(model));
    }

    [Fact]
    public async Task InstallAsync_CancellationLeavesPartialForResume()
    {
        var root = CreateTempDirectory();
        var bytes = Encoding.UTF8.GetBytes("partial-content");
        var model = CreateModel(bytes, bytes);
        using var cancellation = new CancellationTokenSource();
        using var manager = new OcrModelManager(root, new CancellingHandler(bytes, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync(model, cancellationToken: cancellation.Token));

        Assert.True(File.Exists(Path.Combine(root, ".downloads", model.Id, model.Version, "det", "inference.onnx.part")));
        Assert.False(Directory.Exists(Path.Combine(root, model.Id, model.Version)));
    }

    private static OcrModelDescriptor CreateModel(byte[] det, byte[] rec)
    {
        return new(
            "test-model",
            "1",
            "Test",
            "Test",
            "Apache-2.0",
            det.Length + rec.Length,
            new[]
            {
                Artifact("det/inference.onnx", det, "https://example.invalid/det-inference"),
                Artifact("rec/inference.onnx", rec, "https://example.invalid/rec-inference")
            });
    }

    private static OcrModelArtifact Artifact(string path, byte[] bytes, string uri) => new(
            path,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)),
            new Uri(uri));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "quicktranslate-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MapHandler(Dictionary<string, byte[]> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!content.TryGetValue(request.RequestUri!.ToString(), out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RangeHandler(
        OcrModelDescriptor model,
        byte[] det,
        byte[] rec) : HttpMessageHandler
    {
        public long? DetRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isDet = request.RequestUri == model.Artifacts[0].DownloadUri;
            var bytes = isDet ? det : rec;
            var start = request.Headers.Range?.Ranges.Single().From;
            if (isDet)
                DetRangeStart = start;
            if (start is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

            var remaining = bytes[(int)start.Value..];
            var content = new ByteArrayContent(remaining);
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                start.Value, bytes.Length - 1, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }
    }

    private sealed class CancellingHandler(byte[] bytes, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellation.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancelAfterFirstReadStream(bytes, cancellation))
            };
        }
    }

    private sealed class CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        private bool _read;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                var count = Math.Min(buffer.Length, Math.Max(1, Length > 0 ? 1 : 0));
                var read = base.Read(buffer[..count].Span);
                cancellation.Cancel();
                return ValueTask.FromResult(read);
            }
            return ValueTask.FromCanceled<int>(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(true));
        }
    }
}
