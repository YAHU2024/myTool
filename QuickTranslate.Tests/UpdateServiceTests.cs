using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using AutoUpdaterDotNET;
using QuickTranslate.Models;
using QuickTranslate.Services;
using Xunit;

namespace QuickTranslate.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly FakeAutoUpdaterAdapter _fakeAdapter = new();

    public UpdateServiceTests()
    {
        UpdateService.SetAdapterForTesting(_fakeAdapter);
        UpdateService.ResetPendingState();
    }

    public void Dispose()
    {
        UpdateService.ResetPendingState();
        UpdateService.ShowConfirmDialogForTesting = null;
        // Restore production adapter (null triggers creation of real one on next Configure)
        UpdateService.SetAdapterForTesting(new AutoUpdaterAdapter());
    }
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
    public void RequireAuthenticodeSignature_DefaultsToFalse()
    {
        // Safety: Authenticode enforcement must be OFF by default
        // so that users without a code-signing certificate are not
        // blocked from receiving updates.
        var settings = new AppSettings();
        Assert.False(settings.RequireAuthenticodeSignature);
    }

    [Fact]
    public void RequireAuthenticodeSignature_CanBeEnabled()
    {
        var settings = new AppSettings { RequireAuthenticodeSignature = true };
        Assert.True(settings.RequireAuthenticodeSignature);
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
    public void VersionXml_ContainsSignerElement()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var signer = doc.Root!.Element("signer");

        Assert.NotNull(signer);
        var subject = signer!.Element("subject");
        Assert.NotNull(subject);
        Assert.False(string.IsNullOrWhiteSpace(subject!.Value),
            "signer/subject element must not be empty — it defines the expected Authenticode publisher");
    }

    [Fact]
    public void VersionXml_SignerSubjectMatchesExpectedPublisher()
    {
        var xmlPath = FindVersionXml();
        var doc = XDocument.Load(xmlPath);
        var subject = doc.Root!.Element("signer")!.Element("subject")!.Value.Trim();

        // The expected publisher constant in UpdateService.cs is "YaHu"
        // (case-insensitive substring match). The version.xml signer element
        // must contain this value.
        Assert.Contains("YaHu", subject, StringComparison.OrdinalIgnoreCase);
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

    // ─── P2-1: Late-callback isolation tests ───────────────────────
    //
    // Background: When Check A times out, the timeout handler originally
    // cleared _pending, allowing Check B to start immediately.  If A's
    // AutoUpdater callback then arrived late, it would read B's TCS from
    // _pending, complete it with A's stale result, kill B's timeout timer,
    // and clear _pending so B's real callback is lost.
    //
    // Fix: Timeout keeps _pending set.  A late callback finds the TCS
    // already completed (Task.IsCompleted=true) and only updates caches.
    // A cleanup timer (30s grace) clears _pending if the callback
    // never arrives.  No new check can start while _pending is held.

    [Fact]
    public async Task Callback_CompletesCorrectCheck_NormalPath()
    {
        // Normal flow: callback arrives before timeout
        var task = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.False(task.IsCompleted);

        _fakeAdapter.FireUpdateAvailable("2.0.0");
        var result = await task;

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("2.0.0", result.NewVersion);
    }

    [Fact]
    public async Task Error_Callback_CompletesCorrectCheck_NormalPath()
    {
        var task = UpdateService.CheckAsync(autoShowUpdateForm: false);

        _fakeAdapter.FireError(new InvalidOperationException("network down"));
        var result = await task;

        Assert.Equal(UpdateCheckOutcome.Error, result.Outcome);
    }

    [Fact]
    public async Task UpToDate_Callback_CompletesCorrectCheck_NormalPath()
    {
        var task = UpdateService.CheckAsync(autoShowUpdateForm: false);

        _fakeAdapter.FireUpToDate();
        var result = await task;

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task Check_Blocked_While_Pending_Is_Retained()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(1, _fakeAdapter.StartCallCount);

        // Check B: Skipped because A is still in flight
        var resultB = await UpdateService.CheckAsync(autoShowUpdateForm: true);
        Assert.Equal(UpdateCheckOutcome.Skipped, resultB.Outcome);

        // Check C: Same
        var resultC = await UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(UpdateCheckOutcome.Skipped, resultC.Outcome);

        // Complete A normally — this clears _pending
        _fakeAdapter.FireUpToDate();
        var resultA = await taskA;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultA.Outcome);

        // Now a new check should work
        var taskD = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(2, _fakeAdapter.StartCallCount);
        _fakeAdapter.FireUpToDate();
        var resultD = await taskD;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultD.Outcome);
    }

    [Fact]
    public async Task Timeout_Retains_Pending_AndBlocks_SubsequentCheck()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);

        // Simulate timeout (completes TCS with Timeout, keeps _pending)
        UpdateService.SimulateTimeoutForTesting();

        // taskA should already be completed with Timeout
        Assert.True(taskA.IsCompleted);
        var resultA = await taskA;
        Assert.Equal(UpdateCheckOutcome.Timeout, resultA.Outcome);

        // Check B: MUST be Skipped because _pending is still held for A
        var resultB = await UpdateService.CheckAsync(autoShowUpdateForm: true);
        Assert.Equal(UpdateCheckOutcome.Skipped, resultB.Outcome);

        // Simulate cleanup timer — now _pending is cleared
        UpdateService.SimulateCleanupForTesting();

        // Check C: should now work
        var taskC = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(2, _fakeAdapter.StartCallCount);
        _fakeAdapter.FireUpToDate();
        var resultC = await taskC;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultC.Outcome);
    }

    [Fact]
    public async Task LateCallback_DoesNotCompleteSubsequentCheck_AfterTimeout()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);

        // A times out (TCS completed, _pending retained)
        UpdateService.SimulateTimeoutForTesting();

        // Check B: Skipped
        var resultB = await UpdateService.CheckAsync(autoShowUpdateForm: true);
        Assert.Equal(UpdateCheckOutcome.Skipped, resultB.Outcome);

        // A's late callback arrives — _pending is still tcsA (already completed)
        // isLate should be true (Task.IsCompleted), so only cache is updated,
        // NOT the TCS (which is already completed with Timeout)
        _fakeAdapter.FireUpdateAvailable("2.0.0");

        // TCS was already completed with Timeout — TrySetResult in callback returns false
        var resultA2 = await taskA;
        Assert.Equal(UpdateCheckOutcome.Timeout, resultA2.Outcome);
        Assert.Null(resultA2.NewVersion);

        // After A's late callback clears _pending, Check C should work
        var taskC = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(2, _fakeAdapter.StartCallCount);
        _fakeAdapter.FireUpToDate();
        var resultC = await taskC;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultC.Outcome);
    }

    [Fact]
    public async Task LateCallback_UpdatesCache_EvenWhenTcsAlreadyCompleted()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);

        // A times out
        UpdateService.SimulateTimeoutForTesting();

        // A's late callback with update info
        _fakeAdapter.FireUpdateAvailable("2.0.0");

        // A's TCS should still show Timeout
        var resultA = await taskA;
        Assert.Equal(UpdateCheckOutcome.Timeout, resultA.Outcome);

        // Cache should have been updated by the late callback.
        // Use test hook to avoid creating real WPF window in test context.
        UpdateService.ShowConfirmDialogForTesting = (_, _) => true;
        try
        {
            Assert.True(UpdateService.ShowUpdateFormForLastCheck());
        }
        finally
        {
            UpdateService.ShowConfirmDialogForTesting = null;
        }
    }

    [Fact]
    public async Task LateCallback_AfterCleanup_OnlyUpdatesCache()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);

        // A times out
        UpdateService.SimulateTimeoutForTesting();

        // Cleanup timer fires before A's callback — _pending = null
        // In production, this only happens after 10 min grace period,
        // by which time the AutoUpdater HTTP request has timed out
        // and its callback has already fired. The cleanup timer exists
        // solely to prevent a permanent deadlock if the callback
        // somehow never arrives.
        UpdateService.SimulateCleanupForTesting();

        // Check B can now start
        var taskB = UpdateService.CheckAsync(autoShowUpdateForm: false);
        Assert.Equal(2, _fakeAdapter.StartCallCount);

        // B's callback arrives before any stale callback (as expected in
        // production: AutoUpdater request for A was cancelled by B's Start).
        _fakeAdapter.FireUpToDate();
        var resultB = await taskB;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultB.Outcome);
    }

    [Fact]
    public async Task ConsecutiveChecks_Work_AfterNormalCompletion()
    {
        // First check
        var task1 = UpdateService.CheckAsync(autoShowUpdateForm: false);
        _fakeAdapter.FireUpToDate();
        await task1;

        // Second check — should start cleanly
        var task2 = UpdateService.CheckAsync(autoShowUpdateForm: false);
        _fakeAdapter.FireUpdateAvailable("2.0.0");
        var result2 = await task2;
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result2.Outcome);

        // Third check
        var task3 = UpdateService.CheckAsync(autoShowUpdateForm: false);
        _fakeAdapter.FireError(new InvalidOperationException("test error"));
        var result3 = await task3;
        Assert.Equal(UpdateCheckOutcome.Error, result3.Outcome);
    }

    [Fact]
    public async Task LateError_Callback_DoesNotAffect_PendingCheck()
    {
        // Check A starts
        var taskA = UpdateService.CheckAsync(autoShowUpdateForm: false);

        // A times out
        UpdateService.SimulateTimeoutForTesting();

        // A's late error callback
        _fakeAdapter.FireError(new InvalidOperationException("late network error"));

        // TCS still has Timeout result
        var resultA = await taskA;
        Assert.Equal(UpdateCheckOutcome.Timeout, resultA.Outcome);

        // After callback clears _pending, new check works
        var taskB = UpdateService.CheckAsync(autoShowUpdateForm: false);
        _fakeAdapter.FireUpToDate();
        var resultB = await taskB;
        Assert.Equal(UpdateCheckOutcome.UpToDate, resultB.Outcome);
    }

    [Fact]
    public async Task StartFailed_ClearsPending_AndReturnsError_WhenAdapterThrows()
    {
        // Reset and inject an adapter that throws on Start
        UpdateService.ResetPendingState();

        var throwingAdapter = new ThrowingAutoUpdaterAdapter();
        UpdateService.SetAdapterForTesting(throwingAdapter);

        var task = UpdateService.CheckAsync(autoShowUpdateForm: false);
        var result = await task;

        Assert.Equal(UpdateCheckOutcome.Error, result.Outcome);

        // After error, a new check should work (pending was cleared)
        UpdateService.SetAdapterForTesting(_fakeAdapter);
        UpdateService.ResetPendingState();

        var task2 = UpdateService.CheckAsync(autoShowUpdateForm: false);
        _fakeAdapter.FireUpToDate();
        var result2 = await task2;
        Assert.Equal(UpdateCheckOutcome.UpToDate, result2.Outcome);
    }

    // ─── End of P2-1 tests ────────────────────────────────────────

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
