using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using QuickTranslate.Helpers;

namespace QuickTranslate.Core
{
    internal sealed record FocusedAutomationContext(
        int ProcessId,
        string AutomationId,
        string ClassName,
        string ControlType);

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
    /// UI Automation 选区定位器 - 通过 TextPattern 获取选中文本的精确屏幕坐标。
    /// 注意：UIA COM 调用存在原生层崩溃风险（AccessViolationException 0xc0000005），
    /// 内置熔断器：连续失败后自动禁用，防止反复触发不可恢复的进程崩溃。
    /// </summary>
    public static class SelectionLocator
    {
        private static readonly UiaCircuitBreaker SelectionCircuit = new("selection");
        private static readonly UiaCircuitBreaker FocusCircuit = new("focus");

        /// <summary>
        /// 异步获取选中文本边界（在后台 STA 线程执行 UIA 调用，带超时保护 + 熔断器）
        /// </summary>
        public static Task<SelectionLocation?> TryGetSelectionBoundsAsync(int timeoutMs = 2000, CancellationToken cancellationToken = default)
        {
            if (SelectionCircuit.IsDisabled)
            {
                Logger.Debug("SelectionLocator", "uia.selection_circuit_open");
                return Task.FromResult<SelectionLocation?>(null);
            }
            return RunOnSTAThread(
                () => TryGetSelectionBounds(),
                timeoutMs,
                cancellationToken,
                SelectionCircuit);
        }

        internal static Task<FocusedAutomationContext?> TryGetFocusedAutomationContextAsync(
            int timeoutMs = 350,
            CancellationToken cancellationToken = default)
        {
            if (FocusCircuit.IsDisabled)
                return Task.FromResult<FocusedAutomationContext?>(null);

            return RunOnSTAThread(
                TryGetFocusedAutomationContext,
                timeoutMs,
                cancellationToken,
                FocusCircuit);
        }

        /// <summary>
        /// 在独立 STA 线程上执行 UIA 操作，避免阻塞 UI 线程。
        /// 超时后返回 null，防止 UIA 挂起导致鼠标卡顿。
        /// 异常时触发熔断器计数。
        /// </summary>
        private static Task<T?> RunOnSTAThread<T>(
            Func<T?> func,
            int timeoutMs,
            CancellationToken cancellationToken,
            UiaCircuitBreaker circuit) where T : class
        {
            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    var result = func();
                    if (tcs.TrySetResult(result))
                        circuit.RecordSuccess();
                }
                catch (Exception ex)
                {
                    if (tcs.TrySetResult(null))
                        circuit.RecordFailure(ex.GetType().Name);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "UIA_Worker";
            thread.Start();

            // 超时保护：避免 UIA 跨进程调用挂起
            Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                if (tcs.TrySetResult(null))
                    circuit.RecordFailure("Timeout");
            });
            if (cancellationToken.CanBeCanceled) cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            return tcs.Task;
        }
        // ⚠️ TryGetSelectedText 已弃用 —— TextPatternRange.GetText(-1) 在部分应用中
        // 触发 AccessViolationException(0xc0000005) 导致进程不可恢复崩溃。
        // 文本获取已改为纯剪贴板方案（ClipboardHelper）。

        private static FocusedAutomationContext? TryGetFocusedAutomationContext()
        {
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null)
                return null;

            var automationIds = new List<string>();
            var classNames = new List<string>();
            var controlTypes = new List<string>();
            var current = focusedElement;
            const int maxAncestorDepth = 8;
            for (var depth = 0; current != null && depth < maxAncestorDepth; depth++)
            {
                AddDistinct(automationIds, current.Current.AutomationId);
                AddDistinct(classNames, current.Current.ClassName);
                AddDistinct(controlTypes, current.Current.ControlType?.ProgrammaticName);
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }

            return new FocusedAutomationContext(
                focusedElement.Current.ProcessId,
                string.Join("|", automationIds),
                string.Join("|", classNames),
                string.Join("|", controlTypes));
        }

        private static void AddDistinct(List<string> values, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
        }

        /// <summary>
        /// 尝试通过 UI Automation 获取选中文本的精确屏幕坐标。
        /// 需在 STA 线程上调用。
        /// 失败时返回 null，由调用方降级处理。
        /// </summary>
        public static SelectionLocation? TryGetSelectionBounds()
        {
            // 获取当前焦点控件
            var focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null)
            {
                Logger.Debug("SelectionLocator", "无法获取焦点元素");
                return null;
            }

            // 检查是否支持 TextPattern。能力缺失不是 UIA 故障，不触发熔断。
            if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
            {
                Logger.Debug("SelectionLocator", "焦点元素不支持 TextPattern");
                return null;
            }

            var textPattern = patternObj as TextPattern;
            if (textPattern == null)
            {
                Logger.Debug("SelectionLocator", "TextPattern 获取失败");
                return null;
            }

            // 获取选区
            var selections = textPattern.GetSelection();
            if (selections == null || selections.Length == 0)
            {
                Logger.Debug("SelectionLocator", "无选区");
                return null;
            }

            // 取第一个选区的边界矩形
            var selection = selections[0];
            var rects = selection.GetBoundingRectangles();
            if (rects == null || rects.Length == 0)
            {
                Logger.Debug("SelectionLocator", "无边界矩形");
                return null;
            }

            // 解析所有行的矩形，计算整体边界和最后一行末端
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            Rect? lastLineRect = null;

            foreach (var rect in rects)
            {
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || !double.IsFinite(rect.X) || !double.IsFinite(rect.Y)) continue;

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
                Logger.Debug("SelectionLocator", "无有效矩形");
                return null;
            }

            // 最后一行末端右上角外侧坐标（右上角上方）。
            // UIA returns physical screen pixels; keep that contract for the
            // red dot and floating HWND positioning path.
            var endPoint = new Point(lastLineRect.Value.Right, lastLineRect.Value.Y);

            var bounds = new Rect(minX, minY, maxX - minX, maxY - minY);

            Logger.Debug("SelectionLocator", $"UIA 定位成功: EndPoint=({endPoint.X:F0},{endPoint.Y:F0}), Bounds={bounds}");

            return new SelectionLocation
            {
                IsValid = true,
                Bounds = bounds,
                EndPoint = endPoint,
                FallbackPoint = endPoint
            };
        }

    }
}
