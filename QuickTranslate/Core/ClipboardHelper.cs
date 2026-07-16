using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 剪贴板操作辅助 - 模拟 Ctrl+C 获取选中文本
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// 获取当前选中的文本（通过模拟 Ctrl+C）
        /// </summary>
        /// <returns>选中的文本，若无选中文本则返回 null</returns>
        public static async Task<string?> GetSelectedTextAsync()
        {
            string? originalText = null;
            string? result = null;

            // 在 STA 线程中操作剪贴板
            var tcs = new TaskCompletionSource<string?>();

            var staThread = new Thread(() =>
            {
                try
                {
                    // 1. 保存当前剪贴板内容
                    if (Clipboard.ContainsText())
                    {
                        originalText = Clipboard.GetText();
                    }

                    // 2. 清空剪贴板
                    Clipboard.Clear();

                    // 3. 模拟 Ctrl+C（按下 Ctrl -> 按下 C -> 释放 C -> 释放 Ctrl）
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    Win32Api.keybd_event(Win32Api.VK_C, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(30);
                    Win32Api.keybd_event((byte)Win32Api.VK_CONTROL, 0, Win32Api.KEYEVENTF_KEYUP, UIntPtr.Zero);

                    // 4. 等待剪贴板更新
                    Thread.Sleep(150);

                    // 5. 读取剪贴板
                    if (Clipboard.ContainsText())
                    {
                        result = Clipboard.GetText();
                    }

                    // 6. 恢复原始剪贴板内容
                    if (!string.IsNullOrEmpty(originalText))
                    {
                        Clipboard.SetText(originalText);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"剪贴板操作失败: {ex.Message}");
                    // 尝试恢复剪贴板
                    try
                    {
                        if (!string.IsNullOrEmpty(originalText))
                        {
                            Clipboard.SetText(originalText);
                        }
                    }
                    catch { }
                }
                finally
                {
                    tcs.SetResult(result);
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            return await tcs.Task;
        }
    }
}
