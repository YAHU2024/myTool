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
    /// - 零污染检测：通过 GetClipboardSequenceNumber 检测 Ctrl+C 是否生效，不写任何内容到剪贴板
    /// - 快速失败：100ms 内序列号未变 → 无选中文本，立即退出
    /// - 剪贴板恢复增强：5 次重试 x 100ms，最大确保原始内容恢复
    /// 注意：UIA TextPattern.GetText 已移除 —— 该 COM 调用在部分应用中触发
    /// AccessViolationException(0xc0000005) 导致进程不可恢复崩溃。
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>哨兵前缀，用于精准识别哨兵内容</summary>
        public const string SentinelPrefix = "QT_S_";

        // ★ 全局剪贴板互斥锁：防止并发操作互相锁死剪贴板
        private static readonly SemaphoreSlim _clipboardLock = new(1, 1);

        /// <summary>
        /// 获取当前选中的文本。
        /// 全局互斥：如果已有操作进行中，直接返回 null（不排队等待）。
        /// 通过序列号检测 Ctrl+C 是否生效，不写任何内容到剪贴板。
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
        /// ★ 不写哨兵，通过 GetClipboardSequenceNumber 检测 Ctrl+C 是否生效。
        /// 彻底消除哨兵残留可能性。
        /// </summary>
        private static async Task<string?> TryGetTextViaClipboardAsync()
        {
            var tcs = new TaskCompletionSource<string?>();
            var opStart = DateTime.UtcNow;

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
                        // 如果原内容本身就是旧版哨兵格式（历史残留），不保存
                        if (originalText != null && IsSentinel(originalText))
                        {
                            Logger.Warn("ClipboardHelper", $"[剪贴板] 检测到历史哨兵残留，跳过保存");
                            originalText = null;
                        }
                    }

                    // 2. 记录剪贴板序列号（★ 不写哨兵，零污染）
                    var seqBefore = Win32Api.GetClipboardSequenceNumber();
                    Logger.Debug("ClipboardHelper", $"[剪贴板] 序列号 before={seqBefore}");

                    // 3. 模拟 Ctrl+C
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);

                    // 4. 轮询阶段：最多 500ms，等待剪贴板序列号变化
                    //    ★ 快速失败：100ms 内序列号未变化 → 无选中文本，立即退出
                    var pollStart = DateTime.UtcNow;
                    var deadline = pollStart.AddMilliseconds(500);
                    var fastFailChecked = false;
                    var fastFailed = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        var seqNow = Win32Api.GetClipboardSequenceNumber();
                        if (seqNow != seqBefore)
                        {
                            // 序列号变化了！等待稳定后读取（防止剪贴板管理器竞态）
                            Thread.Sleep(30);
                            try
                            {
                                if (Clipboard.ContainsText())
                                {
                                    var t = Clipboard.GetText();
                                    if (!string.IsNullOrWhiteSpace(t))
                                    {
                                        result = t;
                                        var elapsed = (DateTime.UtcNow - pollStart).TotalMilliseconds;
                                        Logger.Debug("ClipboardHelper", $"[剪贴板] 获取成功({elapsed:F0}ms): 长度={t.Length}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("ClipboardHelper", $"[剪贴板] 读取异常: {ex.Message}");
                            }
                            break;
                        }

                        // ★ 快速失败：100ms 后序列号未变 → 无选中文本
                        if (!fastFailChecked && (DateTime.UtcNow - pollStart).TotalMilliseconds >= 100)
                        {
                            fastFailChecked = true;
                            fastFailed = true;
                            Logger.Debug("ClipboardHelper", $"[剪贴板] 快速失败(100ms): 序列号未变化，无选中文本");
                            break;
                        }

                        Thread.Sleep(20);
                    }

                    // 5. 如果轮询未成功且不是因为快速失败，再固定等待 300ms（兜底）
                    if (result == null && !fastFailed)
                    {
                        Logger.Debug("ClipboardHelper", $"[剪贴板] 轮询 500ms 未成功，进入兜底等待 300ms");
                        Thread.Sleep(300);
                        var seqAfter = Win32Api.GetClipboardSequenceNumber();
                        if (seqAfter != seqBefore)
                        {
                            try
                            {
                                if (Clipboard.ContainsText())
                                {
                                    var t = Clipboard.GetText();
                                    if (!string.IsNullOrWhiteSpace(t))
                                    {
                                        result = t;
                                        Logger.Debug("ClipboardHelper", $"[剪贴板] 兜底等待成功: 长度={t.Length}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("ClipboardHelper", $"[剪贴板] 兜底读取异常: {ex.Message}");
                            }
                        }
                        else
                        {
                            Logger.Debug("ClipboardHelper", $"[剪贴板] 兜底等待后序列号仍未变化，Ctrl+C 模拟失败");
                        }
                    }

                    // 6. 恢复原始剪贴板内容（Ctrl+C 成功时才需要恢复）
                    //    即使恢复失败，选中文本留在剪贴板也比丢失原始内容好
                    //    （用户至少可以手动 Ctrl+Z 或重新复制）
                    if (result != null && !string.IsNullOrEmpty(originalText))
                    {
                        bool restored = false;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                Clipboard.SetText(originalText);
                                restored = true;
                                break;
                            }
                            catch
                            {
                                if (attempt < 4) Thread.Sleep(100);
                            }
                        }
                        if (!restored)
                            Logger.Warn("ClipboardHelper", $"[剪贴板] 原始内容恢复失败(5次重试均失败)，选中文本留在剪贴板");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("ClipboardHelper", $"[剪贴板] 操作异常: {ex.Message}");
                }
                finally
                {
                    var finalMs = (DateTime.UtcNow - opStart).TotalMilliseconds;
                    Logger.Debug("ClipboardHelper", $"[剪贴板] 操作结束: 获取结果={(result != null ? $"成功(长度{result.Length})" : "失败")}, 总耗时 {finalMs:F0}ms");
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
        /// 检查字符串是否为哨兵格式（QT_S_ 前缀 + 32位十六进制 GUID）
        /// </summary>
        public static bool IsSentinel(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith(SentinelPrefix))
                return false;
            var hex = s.Substring(SentinelPrefix.Length);
            if (hex.Length != 32) return false;
            foreach (var c in hex)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 启动时清扫：如果剪贴板中有旧版残留哨兵（QT_S_ 前缀），清除它。
        /// 仅用于兼容旧版本，后续可移除。
        /// </summary>
        public static void CleanResidualOnStartup()
        {
            Task.Run(() =>
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            var text = Clipboard.GetText();
                            if (IsSentinel(text))
                            {
                                var sid = text.Substring(text.Length - 8);
                                Clipboard.Clear();
                                Logger.Info("ClipboardHelper", $"[哨兵生命周期] 启动时清除残留哨兵: {sid}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("ClipboardHelper", $"[哨兵生命周期] 启动时清扫异常: {ex.Message}");
                    }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
                t.Join(2000);
            });
        }
    }
}
