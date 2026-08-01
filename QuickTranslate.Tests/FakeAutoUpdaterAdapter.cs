using System;
using AutoUpdaterDotNET;
using QuickTranslate.Services;

namespace QuickTranslate.Tests;

/// <summary>
/// 可控的假 AutoUpdater 适配器，用于确定性模拟超时和迟到回调。
/// </summary>
internal sealed class FakeAutoUpdaterAdapter : IAutoUpdaterAdapter
{
    private EventHandler<UpdateInfoEventArgs>? _handler;

    /// <summary>已调用 Start 的次数。</summary>
    public int StartCallCount { get; private set; }

    /// <summary>最近一次 Start 的 URL。</summary>
    public string? LastUrl { get; private set; }

    public void Start(string url)
    {
        StartCallCount++;
        LastUrl = url;
    }

    public event EventHandler<UpdateInfoEventArgs>? CheckForUpdateCompleted
    {
        add => _handler += value;
        remove => _handler -= value;
    }

    /// <summary>是否有订阅者。</summary>
    public bool HasSubscribers => _handler is not null;

    /// <summary>
    /// 手动触发版本检查完成事件，模拟 AutoUpdater 回调。
    /// </summary>
    /// <param name="args">要传递的更新事件参数（可为 null 表示不触发）。</param>
    public void FireCheckCompleted(UpdateInfoEventArgs args)
    {
        _handler?.Invoke(this, args);
    }

    /// <summary>
    /// 便捷方法：触发“发现更新”回调。
    /// </summary>
    public void FireUpdateAvailable(string newVersion = "2.0.0",
        string installedVersion = "1.0.0",
        string downloadUrl = "https://example.com/update.exe",
        bool mandatory = false)
    {
        FireCheckCompleted(new UpdateInfoEventArgs
        {
            IsUpdateAvailable = true,
            CurrentVersion = newVersion,
            InstalledVersion = new Version(installedVersion),
            DownloadURL = downloadUrl,
            Mandatory = new Mandatory { Value = mandatory, UpdateMode = Mode.Normal }
        });
    }

    /// <summary>
    /// 便捷方法：触发“已是最新版本”回调。
    /// </summary>
    public void FireUpToDate(string installedVersion = "1.0.0")
    {
        FireCheckCompleted(new UpdateInfoEventArgs
        {
            IsUpdateAvailable = false,
            InstalledVersion = new Version(installedVersion)
        });
    }

    /// <summary>
    /// 便捷方法：触发“检查出错”回调。
    /// </summary>
    public void FireError(Exception error)
    {
        FireCheckCompleted(new UpdateInfoEventArgs
        {
            IsUpdateAvailable = false,
            Error = error
        });
    }
}

/// <summary>
/// 在 Start 时抛异常的假适配器，用于测试异常处理路径。
/// </summary>
internal sealed class ThrowingAutoUpdaterAdapter : IAutoUpdaterAdapter
{
    private EventHandler<UpdateInfoEventArgs>? _handler;

    public void Start(string url)
    {
        throw new InvalidOperationException("Simulated start failure");
    }

    public event EventHandler<UpdateInfoEventArgs>? CheckForUpdateCompleted
    {
        add => _handler += value;
        remove => _handler -= value;
    }
}
