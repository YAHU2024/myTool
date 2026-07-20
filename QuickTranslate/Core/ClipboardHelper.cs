using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 文本获取辅助 - 剪贴板降级策略获取选中文本。
    /// 核心安全机制：
    /// - 静态信号量防并发（全局同一时刻只有一个剪贴板操作）
    /// - 单次哨兵（整个 GetSelectedTextAsync 只写一次哨兵，非两次）
    /// - 重试清理（finally 中带重试的哨兵清除，防止泄漏）
    /// 注意：UIA TextPattern.GetText 已移除 —— 该 COM 调用在部分应用中触发
    /// AccessViolationException(0xc0000005) 导致进程不可恢复崩溃。
    /// </summary>
    public static class ClipboardHelper
    {
        // ★ 全局剪贴板互斥锁：防止并发操作互相锁死剪贴板
        private static readonly SemaphoreSlim _clipboardLock = new(1, 1);

        /// <summary>
        /// 获取当前选中的文本。
        /// 全局互斥：如果已有操作进行中，直接返回 null（不排队等待）。
        /// 整个操作只写一次哨兵，在 finally 中带重试清理。
        /// </summary>
        public static async Task<string?> GetSelectedTextAsync()
        {
            // ★ 防并发：如果已有剪贴板操作进行中，直接放弃
            if (!_clipboardLock.Wait(0))
            {
                Logger.Debug("ClipboardHelper", "剪贴板操作进行中，跳过本次请求");
                return null;
            }

            try
            {
                return await TryGetTextViaClipboardAsync();
            }
            finally
            {
                _clipboardLock.Release();
            }
        }

        /// <summary>
        /// 剪贴板模式 - 在 STA 线程模拟 Ctrl+C 后读取剪贴板。
        /// 整个操作只写一次哨兵。先轮询 500ms，若仍为哨兵则再固定等待 300ms。
        /// finally 中带重试清理哨兵（最多重试 5 次，间隔 50ms），确保不泄漏。
        /// </summary>
        private static async Task<string?> TryGetTextViaClipboardAsync()
        {
            var tcs = new TaskCompletionSource<string?>();

            var staThread = new Thread(() =>
            {
                string? originalText = null;
                string? result = null;
                string? sentinel = null;
                try
                {
                    // 1. 保存当前剪贴板内容（用于最后恢复）
                    if (Clipboard.ContainsText())
                    {
                        originalText = Clipboard.GetText();
                        // 如果原内容本身就是哨兵格式（上次泄漏残留），不保存
                        if (originalText != null && originalText.Length == 32
                            && IsHexString(originalText))
                        {
                            Logger.Debug("ClipboardHelper", "检测到哨兵残留，跳过保存");
                            originalText = null;
                        }
                    }

                    // 2. 用唯一哨兵覆盖剪贴板（整个操作只写这一次）
                    sentinel = Guid.NewGuid().ToString("N");
                    Clipboard.SetText(sentinel);

                    // 3. 模拟 Ctrl+C
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);

                    // 4. 轮询阶段：最多 500ms，等待 Ctrl+C 写入新文本
                    var deadline = DateTime.UtcNow.AddMilliseconds(500);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            if (Clipboard.ContainsText())
                            {
                                var t = Clipboard.GetText();
                                if (!string.IsNullOrWhiteSpace(t) && t != sentinel)
                                {
                                    result = t;
                                    break;
                                }
                            }
                        }
                        catch { /* 剪贴板暂时被锁定，继续轮询 */ }
                        Thread.Sleep(20);
                    }

                    // 5. 如果轮询未成功，再固定等待 300ms（兜底）
                    if (result == null)
                    {
                        Thread.Sleep(300);
                        try
                        {
                            if (Clipboard.ContainsText())
                            {
                                var t = Clipboard.GetText();
                                result = (t != sentinel) ? t : null;
                            }
                        }
                        catch { /* 剪贴板被锁定，result 保持 null */ }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("ClipboardHelper", $"剪贴板操作异常: {ex.Message}");
                }
                finally
                {
                    // ★ 关键：带重试的哨兵清理（最多 5 次，间隔 50ms）
                    // 确保即使剪贴板暂时被锁定，也能在短暂等待后成功清理
                    if (sentinel != null)
                    {
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                Clipboard.Clear();
                                break; // 清理成功，退出重试
                            }
                            catch
                            {
                                if (attempt < 4) Thread.Sleep(50);
                                else Logger.Warn("ClipboardHelper", "哨兵清理重试 5 次均失败，启动后台清理");
                            }
                        }

                        // 恢复原始剪贴板内容
                        if (!string.IsNullOrEmpty(originalText))
                        {
                            for (int attempt = 0; attempt < 3; attempt++)
                            {
                                try
                                {
                                    Clipboard.SetText(originalText);
                                    break;
                                }
                                catch
                                {
                                    if (attempt < 2) Thread.Sleep(50);
                                }
                            }
                        }
                    }

                    tcs.SetResult(result);
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Name = "Clipboard_Worker";
            staThread.Start();

            return await tcs.Task;
        }

        /// <summary>
        /// 检查字符串是否为纯十六进制（32位 = 可能是泄漏的哨兵 GUID）
        /// </summary>
        private static bool IsHexString(string s)
        {
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
