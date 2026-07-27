using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void CheckForUpdateOnStartup_DefaultsToTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.CheckForUpdateOnStartup);
    }

    [Fact]
    public void CheckForUpdateOnStartup_CanBeDisabled()
    {
        var settings = new AppSettings { CheckForUpdateOnStartup = false };
        Assert.False(settings.CheckForUpdateOnStartup);
    }

    [Fact]
    public void VersionXml_IsWellFormed()
    {
        var xmlPath = FindVersionXml();
        Assert.True(File.Exists(xmlPath), $"version.xml not found at {xmlPath}");

        var doc = XDocument.Load(xmlPath);
        Assert.NotNull(doc.Root);
        Assert.Equal("item", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void VersionXml_ContainsRequiredElements()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var root = doc.Root!;

        var version = root.Element("version")?.Value;
        var url = root.Element("url")?.Value;
        var changelog = root.Element("changelog")?.Value;

        Assert.False(string.IsNullOrWhiteSpace(version), "version element is missing or empty");
        Assert.False(string.IsNullOrWhiteSpace(url), "url element is missing or empty");
        Assert.False(string.IsNullOrWhiteSpace(changelog), "changelog element is missing or empty");
    }

    [Fact]
    public void VersionXml_VersionMatchesCsproj()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var xmlVersion = doc.Root!.Element("version")!.Value;

        var assemblyVersion = Assembly.GetAssembly(typeof(AppSettings))!.GetName().Version!;
        var expected = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";

        Assert.Equal(expected, xmlVersion);
    }

    [Fact]
    public void VersionXml_UrlPointsToFullInstaller()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var url = doc.Root!.Element("url")!.Value;

        Assert.Contains("github.com/YAHU2024/myTool/releases/download/", url);
        Assert.EndsWith("-full.exe", url);
    }

    [Fact]
    public void VersionXml_ContainsSilentInstallArgs()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var args = doc.Root!.Element("args")?.Value;

        Assert.NotNull(args);
        Assert.Contains("/SILENT", args);
        Assert.DoesNotContain("/VERYSILENT", args);
        Assert.Contains("/SUPPRESSMSGBOXES", args);
        Assert.Contains("/NORESTART", args);
    }

    [Theory]
    [InlineData("QuickTranslate-setup.iss")]
    [InlineData("QuickTranslate-setup-full.iss")]
    public void InstallerScript_RestartsAppAfterSilentUpdate(string scriptName)
    {
        var scriptPath = FindRepositoryFile("installer", scriptName);
        var runEntry = File.ReadLines(scriptPath).Single(line =>
            line.StartsWith("Filename:", StringComparison.Ordinal) &&
            line.Contains("{app}\\{#MyAppExeName}", StringComparison.Ordinal));

        Assert.Contains("postinstall", runEntry, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nowait", runEntry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skipifsilent", runEntry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验和缺失或写成占位符会导致所有用户更新失败（AutoUpdater 报 "Checksum differs"），
    /// 因此这里同时断言格式，防止 TODO/示例值被发布出去。
    /// </summary>
    [Fact]
    public void VersionXml_ContainsSha256Checksum()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var checksum = doc.Root!.Element("checksum");

        Assert.NotNull(checksum);
        Assert.Equal("SHA256", checksum!.Attribute("algorithm")?.Value);

        var value = checksum.Value.Trim();
        Assert.Equal(64, value.Length);
        Assert.All(value, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit"));
    }

    [Fact]
    public void VersionXml_ChangelogPointsToReleaseTag()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var version = doc.Root!.Element("version")!.Value;
        var changelog = doc.Root!.Element("changelog")!.Value;

        Assert.Contains($"/releases/tag/v{version}", changelog);
    }

    [Fact]
    public void NoOpPersistenceProvider_NeverStoresSuppressionState()
    {
        var provider = NoOpUpdatePersistenceProvider.Instance;

        provider.SetSkippedVersion(new Version(9, 9, 9));
        provider.SetRemindLater(DateTime.Now.AddDays(1));

        Assert.Null(provider.GetSkippedVersion());
        Assert.Null(provider.GetRemindLater());
    }

    [Fact]
    public void DeferredModalRunner_SchedulesAfterCurrentCallbackAndBlocksDuplicates()
    {
        var queued = new Queue<Action>();
        var runner = new DeferredModalRunner(queued.Enqueue);
        var callCount = 0;

        Assert.True(runner.TrySchedule(() =>
        {
            Assert.True(runner.IsBusy);
            Assert.False(runner.TryRun(() => callCount++));
            callCount++;
        }));

        Assert.True(runner.IsBusy);
        Assert.Equal(0, callCount);
        Assert.False(runner.TrySchedule(() => callCount++));

        queued.Dequeue()();

        Assert.False(runner.IsBusy);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void DeferredModalRunner_ReleasesBusyStateWhenModalThrows()
    {
        var runner = new DeferredModalRunner(action => action());

        Assert.Throws<InvalidOperationException>(() =>
            runner.TryRun(() => throw new InvalidOperationException()));

        Assert.False(runner.IsBusy);
        Assert.True(runner.TryRun(() => { }));
    }

    [Fact]
    public void DeferredModalRunner_ReleasesBusyStateWhenEnqueueThrows()
    {
        var runner = new DeferredModalRunner(_ => throw new InvalidOperationException());

        Assert.Throws<InvalidOperationException>(() => runner.TrySchedule(() => { }));

        Assert.False(runner.IsBusy);
        Assert.True(runner.TryRun(() => { }));
    }

    /// <summary>
    /// 定位 installer/version.xml（从测试输出目录向上查找仓库根目录）
    /// </summary>
    private static string FindVersionXml()
    {
        return FindRepositoryFile("installer", "version.xml");
    }

    private static string FindRepositoryFile(params string[] pathParts)
    {
        var relativePath = Path.Combine(pathParts);
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return candidate;
            // 到达盘符根时返回 null，需在此终止，否则下一轮 Path.Combine 抛异常
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: 相对于测试项目的已知路径
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
    }
}
