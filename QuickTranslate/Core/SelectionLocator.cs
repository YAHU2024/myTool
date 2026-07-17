using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

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
    /// UI Automation 选区定位器 - 通过 TextPattern 获取选中文本的精确屏幕坐标
    /// </summary>
    public static class SelectionLocator
    {
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

                // 最后一行末端右侧坐标
                var endPoint = new Point(lastLineRect.Value.Right, lastLineRect.Value.Y + lastLineRect.Value.Height / 2);

                var bounds = new Rect(minX, minY, maxX - minX, maxY - minY);

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
