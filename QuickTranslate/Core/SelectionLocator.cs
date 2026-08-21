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
        private const int MaxAncestorDepth = 8;
        private const int MaxCandidateCount = 32;
        private const double GestureMatchTolerance = 24;
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

        internal static Task<SelectionLocation?> TryGetSelectionBoundsAsync(
            Point startPoint,
            Point endPoint,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default)
        {
            if (SelectionCircuit.IsDisabled)
            {
                Logger.Debug("SelectionLocator", "uia.selection_circuit_open");
                return Task.FromResult<SelectionLocation?>(null);
            }

            var probePoints = CreateGestureProbePoints(startPoint, endPoint);
            return RunOnSTAThread(
                () => TryGetSelectionBounds(probePoints),
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
            return TryGetSelectionBounds(probePoints: null);
        }

        internal static IReadOnlyList<Point> CreateGestureProbePoints(Point startPoint, Point endPoint)
        {
            if (startPoint == endPoint)
                return [endPoint];

            // The release point most often belongs to the text container that
            // owns the selection, so probe it before the drag origin.
            return [endPoint, startPoint];
        }

        internal static bool IsSelectionNearProbePoints(
            SelectionLocation location,
            IReadOnlyList<Point> probePoints)
        {
            if (!location.IsValid || location.Bounds.IsEmpty || probePoints.Count == 0)
                return false;

            var bounds = location.Bounds;
            bounds.Inflate(GestureMatchTolerance, GestureMatchTolerance);
            return probePoints.Any(bounds.Contains);
        }

        private static SelectionLocation? TryGetSelectionBounds(IReadOnlyList<Point>? probePoints)
        {
            var candidates = GetSelectionCandidates(probePoints);
            if (candidates.Count == 0)
            {
                Logger.Debug("SelectionLocator", "uia.selection_candidates_empty");
                return null;
            }

            var textPatternCandidates = 0;
            foreach (var candidate in candidates)
            {
                SelectionLocation? location;
                try
                {
                    location = TryGetSelectionBounds(candidate, out var supportsTextPattern);
                    if (supportsTextPattern)
                        textPatternCandidates++;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                if (location == null)
                    continue;
                if (probePoints is { Count: > 0 } &&
                    !IsSelectionNearProbePoints(location, probePoints))
                    continue;

                Logger.Debug("SelectionLocator", "uia.selection_bounds_found", new
                {
                    candidate_count = candidates.Count,
                    text_pattern_candidates = textPatternCandidates,
                    probe_count = probePoints?.Count ?? 0
                });
                return location;
            }

            Logger.Debug("SelectionLocator", "uia.selection_bounds_missing", new
            {
                candidate_count = candidates.Count,
                text_pattern_candidates = textPatternCandidates,
                probe_count = probePoints?.Count ?? 0
            });
            return null;
        }

        private static SelectionLocation? TryGetSelectionBounds(
            AutomationElement element,
            out bool supportsTextPattern)
        {
            supportsTextPattern = element.TryGetCurrentPattern(
                TextPattern.Pattern,
                out var patternObject);
            if (!supportsTextPattern || patternObject is not TextPattern textPattern)
                return null;

            var selections = textPattern.GetSelection();
            if (selections == null || selections.Length == 0)
                return null;

            var selection = selections[0];
            var rects = selection.GetBoundingRectangles();
            if (rects == null || rects.Length == 0)
                return null;

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
                return null;

            // 最后一行末端右上角外侧坐标（右上角上方）。
            // UIA returns physical screen pixels; keep that contract for the
            // red dot and floating HWND positioning path.
            var endPoint = new Point(lastLineRect.Value.Right, lastLineRect.Value.Y);

            var bounds = new Rect(minX, minY, maxX - minX, maxY - minY);

            return new SelectionLocation
            {
                IsValid = true,
                Bounds = bounds,
                EndPoint = endPoint,
                FallbackPoint = endPoint
            };
        }

        private static List<AutomationElement> GetSelectionCandidates(
            IReadOnlyList<Point>? probePoints)
        {
            var candidates = new List<AutomationElement>();
            var runtimeIds = new HashSet<string>(StringComparer.Ordinal);
            var seeds = new List<AutomationElement>();

            if (probePoints != null)
            {
                foreach (var point in probePoints)
                {
                    try
                    {
                        AddSeed(AutomationElement.FromPoint(point), seeds);
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }
            }

            try
            {
                AddSeed(AutomationElement.FocusedElement, seeds);
            }
            catch (ElementNotAvailableException)
            {
            }

            // Preserve all direct candidates before ancestor expansion can
            // consume the bounded candidate budget.
            foreach (var seed in seeds)
                AddCandidate(seed, candidates, runtimeIds);
            foreach (var seed in seeds)
                AddCandidateAncestors(seed, candidates, runtimeIds);

            return candidates;
        }

        private static void AddSeed(
            AutomationElement? seed,
            List<AutomationElement> seeds)
        {
            if (seed != null)
                seeds.Add(seed);
        }

        private static void AddCandidateAncestors(
            AutomationElement seed,
            List<AutomationElement> candidates,
            HashSet<string> runtimeIds)
        {
            if (candidates.Count >= MaxCandidateCount)
                return;

            AddAncestorChain(seed, TreeWalker.ControlViewWalker, candidates, runtimeIds);
            AddAncestorChain(seed, TreeWalker.RawViewWalker, candidates, runtimeIds);
        }

        private static void AddAncestorChain(
            AutomationElement seed,
            TreeWalker walker,
            List<AutomationElement> candidates,
            HashSet<string> runtimeIds)
        {
            AutomationElement? current;
            try
            {
                current = walker.GetParent(seed);
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            for (var depth = 1;
                 current != null && depth <= MaxAncestorDepth && candidates.Count < MaxCandidateCount;
                 depth++)
            {
                try
                {
                    AddCandidate(current, candidates, runtimeIds);
                    current = walker.GetParent(current);
                }
                catch (ElementNotAvailableException)
                {
                    return;
                }
            }
        }

        private static void AddCandidate(
            AutomationElement candidate,
            List<AutomationElement> candidates,
            HashSet<string> runtimeIds)
        {
            if (candidates.Count >= MaxCandidateCount)
                return;

            try
            {
                var runtimeId = candidate.GetRuntimeId();
                if (runtimeId.Length == 0 || runtimeIds.Add(string.Join('.', runtimeId)))
                    candidates.Add(candidate);
            }
            catch (ElementNotAvailableException)
            {
            }
        }

    }
}
