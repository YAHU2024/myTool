using QuickTranslate.Core;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public interface IScreenshotCaptureService
{
    OcrImage Capture(ScreenshotRegion region);
}
