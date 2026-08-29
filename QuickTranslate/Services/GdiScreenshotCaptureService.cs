using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QuickTranslate.Core;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// 使用 GDI 屏幕复制捕获物理像素。捕获结果直接符合 OCR 的 BGRA32 契约。
/// </summary>
public sealed class GdiScreenshotCaptureService : IScreenshotCaptureService
{
    private readonly OcrResourceLimits _limits;

    public GdiScreenshotCaptureService(OcrResourceLimits? limits = null)
    {
        _limits = limits ?? OcrResourceLimits.Default;
    }

    public OcrImage Capture(ScreenshotRegion region)
    {
        ValidateRegion(region);

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                region.Left,
                region.Top,
                0,
                0,
                new Size(region.Width, region.Height),
                CopyPixelOperation.SourceCopy);
        }

        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var sourceStride = data.Stride;
            var stride = Math.Abs(sourceStride);
            var rowBytes = checked(stride * region.Height);
            var pixels = new byte[rowBytes];
            if (sourceStride > 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, rowBytes);
            }
            else
            {
                for (var row = 0; row < region.Height; row++)
                {
                    var source = IntPtr.Add(data.Scan0, (region.Height - 1 - row) * sourceStride);
                    Marshal.Copy(source, pixels, row * stride, stride);
                }
            }

            var image = new OcrImage(region.Width, region.Height, stride, pixels);
            image.Validate(_limits);
            return image;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void ValidateRegion(ScreenshotRegion region)
    {
        if (!region.IsValid)
            throw new ArgumentException("截图区域必须大于 0。", nameof(region));
        if (region.Width > _limits.MaxImageDimension || region.Height > _limits.MaxImageDimension)
            throw new ArgumentException("截图区域超过允许的最大边长。", nameof(region));
        if ((long)region.Width * region.Height > _limits.MaxPixelCount)
            throw new ArgumentException("截图区域超过允许的像素总数。", nameof(region));
        if ((long)region.Width * 4 * region.Height > _limits.MaxPayloadBytes)
            throw new ArgumentException("截图区域超过允许的像素载荷。", nameof(region));
    }
}
