using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    /// <summary>
    /// 选中文本位置信息
    /// </summary>
    public class SelectionLocation
    {
        /// <summary>
        /// UIA 是否成功获取到选中文本坐标
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 选中文本整体边界矩形
        /// </summary>
        public Rect Bounds { get; set; }

        /// <summary>
        /// 最后一行末端坐标（红点锚点）
        /// </summary>
        public Point EndPoint { get; set; }

        /// <summary>
        /// 降级用估算坐标（已包含偏移量）
        /// </summary>
        public Point FallbackPoint { get; set; }
    }

    /// <summary>
    /// UI Automation 选区定位器 - 通过 TextPattern 获取选中文本的精确屏幕坐标和文本内容
    /// </summary>
    public static class SelectionLocator
    {
        /// <summary>
        /// 异步获取选中文本（在后台 STA 线程执行 UIA 调用，带超时保护）
        /// </summary>
        public static Task<string?> TryGetSelectedTextAsync(int timeoutMs = 2000)
        {
            return RunOnSTAThread(() => TryGetSelectedText(), timeoutMs);
        }

        /// <summary>
        /// 异步获取选中文本边界（在后台 STA 线程执行 UIA 调用，带超时保护）
        /// </summary>
        public static Task<SelectionLocation?> TryGetSelectionBoundsAsync(int timeoutMs = 2000)
        {
            return RunOnSTAThread(() => TryGetSelectionBounds(), timeoutMs);
        }

        /// <summary>
        /// 在独立 STA 线程上执行 UIA 操作，避免阻塞 UI 线程。
        /// 超时后返回 null，防止 UIA 挂起导致鼠标卡顿。
        /// </summary>
        private static Task<T?> RunOnSTAThread<T>(Func<T?> func, int timeoutMs) where T : class
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SelectionLocator] STA线程UIA异常: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "UIA_Worker";
            thread.Start();

            // 超时保护：避免 UIA 跨进程调用挂起
            Task.Delay(timeoutMs).ContinueWith(_ => tcs.TrySetResult(null));

            return tcs.Task;
        }
        /// <summary>
        /// 尝试通过 UI Automation TextPattern 直接获取选中文本（不依赖剪贴板）。
        /// 需在 STA 线程上调用。
        /// 失败时返回 null，由调用方降级到剪贴板方案。
        /// </summary>
        public static string? TryGetSelectedText()
        {
            try
            {
                var focusedElement = AutomationElement.FocusedElement;
                if (focusedElement == null)
                {
                    Debug.WriteLine("[SelectionLocator] UIA文本获取: 无焦点元素");
                    return null;
                }

                if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                {
                    Debug.WriteLine("[SelectionLocator] UIA文本获取: 焦点元素不支持 TextPattern");
                    return null;
                }

                var textPattern = patternObj as TextPattern;
                if (textPattern == null)
                    return null;

                var selections = textPattern.GetSelection();
                if (selections == null || selections.Length == 0)
                {
                    Debug.WriteLine("[SelectionLocator] UIA文本获取: 无选区");
                    return null;
                }

                var text = selections[0].GetText(-1);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Debug.WriteLine("[SelectionLocator] UIA文本获取: 选区文本为空");
                    return null;
                }

                Debug.WriteLine($"[SelectionLocator] UIA文本获取成功: {text.Length} 字符");
                return text;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelectionLocator] UIA文本获取异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 尝试通过 UI Automation 获取选中文本的精确屏幕坐标。
        /// 需在 STA 线程上调用。
        /// 失败时返回 null，由调用方降级处理。
        /// </summary>
        public static SelectionLocation? TryGetSelectionBounds()
        {
            try
            {
                // 获取当前焦点控件
                var focusedElement = AutomationElement.FocusedElement;
                if (focusedElement == null)
                {
                    Debug.WriteLine("[SelectionLocator] 无法获取焦点元素");
                    return null;
                }

                // 检查是否支持 TextPattern
                if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
                {
                    Debug.WriteLine("[SelectionLocator] 焦点元素不支持 TextPattern");
                    return null;
                }

                var textPattern = patternObj as TextPattern;
                if (textPattern == null)
                {
                    Debug.WriteLine("[SelectionLocator] TextPattern 获取失败");
                    return null;
                }

                // 获取选区
                var selections = textPattern.GetSelection();
                if (selections == null || selections.Length == 0)
                {
                    Debug.WriteLine("[SelectionLocator] 无选区");
                    return null;
                }

                // 取第一个选区的边界矩形
                var selection = selections[0];
                var rects = selection.GetBoundingRectangles();
                if (rects == null || rects.Length == 0)
                {
                    Debug.WriteLine("[SelectionLocator] 无边界矩形");
                    return null;
                }

                // 解析所有行的矩形，计算整体边界和最后一行末端
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                Rect? lastLineRect = null;

                foreach (var rect in rects)
                {
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;

                    minX = Math.Min(minX, rect.X);
                    minY = Math.Min(minY, rect.Y);
                    maxX = Math.Max(maxX, rect.Right);
                    maxY = Math.Max(maxY, rect.Bottom);

                    // 取 Y 值最大（最后一行）的矩形
                    if (lastLineRect == null || rect.Y > lastLineRect.Value.Y)
                    {
                        lastLineRect = rect;
                    }
                }

                if (lastLineRect == null || minX == double.MaxValue)
                {
                    Debug.WriteLine("[SelectionLocator] 无有效矩形");
                    return null;
                }

                // 最后一行末端右上角外侧坐标（右上角上方）
                // UIA 返回物理像素，转换为 WPF 逻辑像素(DIP)
                var endPoint = DpiHelper.PhysicalToLogical(
                    new Point(lastLineRect.Value.Right, lastLineRect.Value.Y));

                var bounds = DpiHelper.PhysicalToLogical(
                    new Rect(minX, minY, maxX - minX, maxY - minY));

                Debug.WriteLine($"[SelectionLocator] UIA 定位成功: EndPoint=({endPoint.X:F0},{endPoint.Y:F0}), Bounds={bounds}");

                return new SelectionLocation
                {
                    IsValid = true,
                    Bounds = bounds,
                    EndPoint = endPoint,
                    FallbackPoint = endPoint
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelectionLocator] UIA 获取选区失败: {ex.Message}");
                return null;
            }
        }
    }
}
