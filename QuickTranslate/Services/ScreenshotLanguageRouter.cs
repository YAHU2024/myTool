namespace QuickTranslate.Services;

public enum ScreenshotSourceLanguage
{
    Unknown,
    English,
    Chinese,
    Japanese
}

public static class ScreenshotLanguageRouter
{
    public static ScreenshotSourceLanguage Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ScreenshotSourceLanguage.Unknown;
        var kana = 0; var han = 0; var latin = 0;
        foreach (var c in text)
        {
            if (c is >= '\u3040' and <= '\u30ff') kana++;
            else if (c is >= '\u3400' and <= '\u9fff') han++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }
        if (kana >= 2 || (kana > 0 && han >= 2)) return ScreenshotSourceLanguage.Japanese;
        if (han >= 2 && han > latin) return ScreenshotSourceLanguage.Chinese;
        if (latin >= 3 && latin >= han) return ScreenshotSourceLanguage.English;
        return ScreenshotSourceLanguage.Unknown;
    }

    public static string? ToWorkerLanguage(this ScreenshotSourceLanguage language) => language switch
    {
        ScreenshotSourceLanguage.English => "en",
        ScreenshotSourceLanguage.Chinese => "zh",
        ScreenshotSourceLanguage.Japanese => "ja",
        _ => null
    };
}
