using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        /// 获取当前选中的文本。请求必须先通过终端复制策略审查。
        /// </summary>
        internal static async Task<string?> GetSelectedTextAsync(CopyRequest request)
        {
            // ★ 防并发：如果已有剪贴板操作进行中，直接放弃
            if (!_clipboardLock.Wait(0))
            {
                Logger.Debug("ClipboardHelper", "剪贴板操作进行中，跳过本次请求");
                return null;
            }

            try
            {
                return await TryGetTextViaClipboardAsync(request);
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
        private static async Task<string?> TryGetTextViaClipboardAsync(CopyRequest request)
        {
            if (request.ActionRisk == CopyActionRisk.PotentialInterrupt)
            {
                Logger.Warn("ClipboardHelper", "clipboard.interrupting_copy_rejected", new
                {
                    terminal_risk = request.TerminalRisk.ToString(),
                    decision = request.DecisionReason.ToString(),
                    evidence = request.SelectionEvidence.ToString()
                });
                return null;
            }

            // 普通复制（含 webview 面板）允许 Ctrl+C；其余终端风险下的 Ctrl+C 一律拒绝，
            // 防止把中断信号注入真实终端。
            if (request.TerminalRisk != TerminalRiskKind.NonTerminal &&
                request.ActionRisk != CopyActionRisk.OrdinaryCopy &&
                request.Shortcut == CopyShortcut.CtrlC &&
                request.DecisionReason != CopyDecisionReason.ExplicitTerminalMapping)
            {
                Logger.Warn("ClipboardHelper", "clipboard.unsafe_terminal_request_rejected", new
                {
                    terminal_risk = request.TerminalRisk.ToString(),
                    decision = request.DecisionReason.ToString()
                });
                return null;
            }

            var tcs = new TaskCompletionSource<string?>();
            var opStart = DateTime.UtcNow;

            var staThread = new Thread(() =>
            {
                string? originalText = null;
                string? result = null;
                uint copiedSequence = 0;
                try
                {
                    // 0. “选中即复制”快速路径：终端应用的选区复制发生在鼠标抬起时
                    //    （OSC 52 / copyOnSelection），文字在注入按键前已在剪贴板中，
                    //    直接采用可避免注入空操作与漫长的等待窗口。只读取、不写入。
                    if (ShouldProbeRecentAutoCopy(request.TerminalRisk) &&
                        TryReadRecentAutoCopiedSelection(out var autoCopiedText))
                    {
                        result = autoCopiedText;
                        return;
                    }

                    if (!WaitForModifiersReleased() || !IsExpectedWindow(request))
                    {
                        Logger.Debug("ClipboardHelper", "[Clipboard] Source window changed or modifier keys are still pressed");
                        return;
                    }

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

                    // 3. Send one marked, ordered input batch only while the original window is foreground.
                    if (!SendCopyShortcut(request.Shortcut))
                    {
                        Logger.Warn("ClipboardHelper", "[Clipboard] SendInput failed");
                        return;
                    }

                    // 4. Wait once for the requested copy command; never retry by injecting another shortcut.
                    //    终端复制落剪贴板可能明显慢于普通应用（如 VSCode 无障碍模式的异步写入），给更长窗口。
                    var pollWindowMs = request.TerminalRisk == TerminalRiskKind.NonTerminal ? 500 : 1500;
                    var fallbackWindowMs = request.TerminalRisk == TerminalRiskKind.NonTerminal ? 300 : 800;
                    var pollStart = DateTime.UtcNow;
                    var deadline = pollStart.AddMilliseconds(pollWindowMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        var seqNow = Win32Api.GetClipboardSequenceNumber();
                        if (seqNow != seqBefore)
                        {
                            if (!IsExpectedWindow(request))
                            {
                                Logger.Debug("ClipboardHelper", "[Clipboard] Foreground changed after copy; discard result");
                                break;
                            }
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
                                        copiedSequence = Win32Api.GetClipboardSequenceNumber();
                                        var elapsed = (DateTime.UtcNow - pollStart).TotalMilliseconds;
                                        Logger.Debug("ClipboardHelper", $"[剪贴板] 获取成功({elapsed:F0}ms): 长度={t.Length}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("ClipboardHelper", "clipboard.read_failed", new { error_type = ex.GetType().Name });
                            }
                            break;
                        }

                        Thread.Sleep(20);
                    }

                    // 5. Give applications with asynchronous clipboard ownership one final read window.
                    if (result == null)
                    {
                        Logger.Debug("ClipboardHelper", $"[剪贴板] 轮询 {pollWindowMs}ms 未成功，进入兜底等待 {fallbackWindowMs}ms");
                        Thread.Sleep(fallbackWindowMs);
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
                                        copiedSequence = Win32Api.GetClipboardSequenceNumber();
                                        Logger.Debug("ClipboardHelper", $"[剪贴板] 兜底等待成功: 长度={t.Length}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("ClipboardHelper", "clipboard.fallback_read_failed", new { error_type = ex.GetType().Name });
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
                    if (result != null && request.RestoreClipboard && originalText != null)
                    {
                        if (Win32Api.GetClipboardSequenceNumber() != copiedSequence)
                        {
                            Logger.Debug("ClipboardHelper", "[Clipboard] Clipboard changed after copy; skip restore");
                        }
                        else
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
                                Logger.Warn("ClipboardHelper", "[Clipboard] Original clipboard restore failed after 5 attempts");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("ClipboardHelper", "clipboard.operation_failed", new { error_type = ex.GetType().Name });
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

        private static bool IsExpectedWindow(CopyRequest request) =>
            request.ExpectedForegroundWindow != IntPtr.Zero &&
            Win32Api.GetForegroundWindow() == request.ExpectedForegroundWindow;

        /// <summary>
        /// “选中即复制”快速路径只对终端风险生效；普通应用的 Ctrl+C 注入
        /// 可靠且带剪贴板恢复，不应被剪贴板旧内容短路。提取为纯函数便于回归测试。
        /// </summary>
        internal static bool ShouldProbeRecentAutoCopy(TerminalRiskKind risk) =>
            risk != TerminalRiskKind.NonTerminal;

        /// <summary>
        /// 探测剪贴板中是否已有目标应用刚自动复制的选区内容。
        /// 只读取不写入；判定依据 SelectionDetector 的鼠标选区基线。
        /// </summary>
        private static bool TryReadRecentAutoCopiedSelection(out string? text)
        {
            text = null;
            try
            {
                var (sequenceAtMouseDown, mouseDownTick, mouseUpTick) = SelectionDetector.LastSelectionBaseline;
                if (!RecentSelectionCopyEvaluator.IsAutoCopySuspected(
                        sequenceAtMouseDown,
                        mouseDownTick,
                        mouseUpTick,
                        Win32Api.GetClipboardSequenceNumber(),
                        Environment.TickCount64))
                    return false;

                if (!Clipboard.ContainsText())
                    return false;

                var candidate = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(candidate))
                    return false;

                text = candidate;
                Logger.Debug("ClipboardHelper", "clipboard.autocopy_selection_reused", new { text_len = text.Length });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("ClipboardHelper", "clipboard.autocopy_probe_failed", new { error_type = ex.GetType().Name });
                return false;
            }
        }

        private static bool WaitForModifiersReleased()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(350);
            while (DateTime.UtcNow < deadline)
            {
                var ctrlDown = (Win32Api.GetAsyncKeyState(Win32Api.VK_CONTROL) & 0x8000) != 0;
                var altDown = (Win32Api.GetAsyncKeyState(0x12) & 0x8000) != 0;
                var shiftDown = (Win32Api.GetAsyncKeyState(0x10) & 0x8000) != 0;
                if (!ctrlDown && !altDown && !shiftDown) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        private static bool SendCopyShortcut(CopyShortcut shortcut)
        {
            const ulong marker = 0x5154_434F_5059_0001;
            var keys = new List<byte>();
            if (shortcut.Ctrl) keys.Add((byte)Win32Api.VK_CONTROL);
            if (shortcut.Alt) keys.Add(0x12);
            if (shortcut.Shift) keys.Add(0x10);

            var inputs = new List<Win32Api.INPUT>();
            foreach (var key in keys) inputs.Add(CreateKeyboardInput(key, false, marker));
            inputs.Add(CreateKeyboardInput(shortcut.Key, false, marker));
            inputs.Add(CreateKeyboardInput(shortcut.Key, true, marker));
            for (var i = keys.Count - 1; i >= 0; i--) inputs.Add(CreateKeyboardInput(keys[i], true, marker));

            var sent = Win32Api.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Win32Api.INPUT>());
            if (sent != inputs.Count)
            {
                Logger.Warn("ClipboardHelper", $"[Clipboard] SendInput failed: sent={sent}/{inputs.Count}, cbSize={Marshal.SizeOf<Win32Api.INPUT>()}, win32Error={Marshal.GetLastWin32Error()}");
            }
            return sent == inputs.Count;
        }

        private static Win32Api.INPUT CreateKeyboardInput(byte key, bool keyUp, ulong marker) => new()
        {
            type = Win32Api.INPUT_KEYBOARD,
            U = new Win32Api.InputUnion
            {
                ki = new Win32Api.KEYBDINPUT
                {
                    wVk = key,
                    dwFlags = keyUp ? Win32Api.KEYEVENTF_KEYUP_EX : 0,
                    dwExtraInfo = (UIntPtr)marker
                }
            }
        };

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
                        Logger.Warn("ClipboardHelper", "clipboard.sentinel_cleanup_failed", new { error_type = ex.GetType().Name });
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
