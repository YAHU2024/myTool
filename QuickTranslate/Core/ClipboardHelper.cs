using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 文本获取辅助 - 二级降级策略获取选中文本
    /// 第1级：剪贴板轮询模式（Ctrl+C + 轮询直到剪贴板内容变化）
    /// 第2级：固定等待兜底（兼容极端情况）
    /// 注意：UIA TextPattern.GetText 已移除 —— 该 COM 调用在部分应用中触发
    /// AccessViolationException(0xc0000005) 导致进程不可恢复崩溃。
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// 获取当前选中的文本（二级降级策略）
        /// </summary>
        public static async Task<string?> GetSelectedTextAsync()
        {
            // 第1级：剪贴板轮询模式（复制成功后立即返回，无需等待固定时长）
            var pollText = await TryGetTextViaClipboardAsync(usePolling: true);
            if (!string.IsNullOrWhiteSpace(pollText))
            {
                Logger.Info("ClipboardHelper", "第1级成功: 剪贴板轮询模式获取文本");
                return pollText;
            }

            // 第2级：固定等待兜底
            var fallbackText = await TryGetTextViaClipboardAsync(usePolling: false);
            if (!string.IsNullOrWhiteSpace(fallbackText))
            {
                Logger.Info("ClipboardHelper", "第2级成功: 固定等待兜底获取文本");
                return fallbackText;
            }

            Logger.Warn("ClipboardHelper", "二级降级均失败，无选中文本");
            return null;
        }

        /// <summary>
        /// 第2/3级：剪贴板模式 - 在 STA 线程模拟 Ctrl+C 后读取剪贴板。
        /// usePolling=true 时轮询直到剪贴板内容发生变化（最多500ms），复制成功即可立即返回；
        /// usePolling=false 时固定等待300ms（兼容极端情况）。
        /// 采用唯一哨兵覆盖剪贴板作为基准，避免"新选中文本恰好等于原剪贴板内容"的误判。
        /// </summary>
        private static async Task<string?> TryGetTextViaClipboardAsync(bool usePolling)
        {
            var tcs = new TaskCompletionSource<string?>();

            var staThread = new Thread(() =>
            {
                string? originalText = null;
                string? result = null;
                try
                {
                    // 1. 保存当前剪贴板内容（用于最后恢复）
                    if (Clipboard.ContainsText())
                    {
                        originalText = Clipboard.GetText();
                    }

                    // 2. 用唯一哨兵覆盖剪贴板，作为"Ctrl+C 是否真正写入"的判定基准
                    var sentinel = Guid.NewGuid().ToString("N");
                    Clipboard.SetText(sentinel);

                    // 3. 模拟 Ctrl+C（按下 Ctrl -> 按下 C -> 释放 C -> 释放 Ctrl）
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);

                    // 4. 等待剪贴板更新
                    if (usePolling)
                    {
                        // 轮询：直到剪贴板内容不再是哨兵（即 Ctrl+C 已写入新文本）
                        var deadline = DateTime.UtcNow.AddMilliseconds(500);
                        while (DateTime.UtcNow < deadline)
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
                            Thread.Sleep(20);
                        }
                    }
                    else
                    {
                        // 固定等待兜底（增加到300ms，对浏览器更友好）
                        Thread.Sleep(300);
                        if (Clipboard.ContainsText())
                        {
                            result = Clipboard.GetText();
                        }
                    }

                    // 5. 校验：若读到的仍是哨兵，说明 Ctrl+C 未生效，视为失败
                    if (result == sentinel)
                    {
                        Logger.Debug("ClipboardHelper", $"剪贴板模式({(usePolling ? "轮询" : "固定等待")}): 检测到哨兵残留，视为失败");
                        result = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("ClipboardHelper", $"剪贴板模式({(usePolling ? "轮询" : "固定等待")})异常: {ex.Message}");
                }
                finally
                {
                    // 无论如何都恢复原始剪贴板
                    RestoreClipboard(originalText);
                    tcs.SetResult(result);
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            return await tcs.Task;
        }

        /// <summary>
        /// 恢复剪贴板内容
        /// </summary>
        private static void RestoreClipboard(string? originalText)
        {
            try
            {
                if (!string.IsNullOrEmpty(originalText))
                {
                    Clipboard.SetText(originalText);
                }
                else
                {
                    Clipboard.Clear();
                }
            }
            catch { }
        }
    }
}
