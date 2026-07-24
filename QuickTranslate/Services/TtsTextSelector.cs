using System.Globalization;
using System.Text;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>
/// Pure helpers for TTS eligibility, text prep, and voice selection.
/// </summary>
public static class TtsTextSelector
{
    public const string VoiceXiaoxiao = "zh-CN-XiaoxiaoNeural";
    public const string VoiceYunxi = "zh-CN-YunxiNeural";
    public const string VoiceJenny = "en-US-JennyNeural";
    public const string VoiceGuy = "en-US-GuyNeural";

    public const string SelectionAuto = "auto";
    public const string SelectionManual = "manual";
    public const string VoiceSourceAuto = "auto";
    public const string VoiceSourceUser = "user";
    public const string VoiceSourceFallback = "fallback";

    internal static bool CanSpeak(ModeResultStatus status, string? rawText, bool ttsEnabled) =>
        ttsEnabled
        && status == ModeResultStatus.Completed
        && !string.IsNullOrWhiteSpace(rawText);

    /// <summary>
    /// Resolved speak request: text, voice, and selection metadata (no spoken body in logs).
    /// </summary>
    public sealed record SpeakPlan(
        string Text,
        string Voice,
        double Rate,
        string LanguageHint,
        string SelectionMode,
        string VoiceSource,
        string? FallbackFrom,
        bool Truncated);

    public static string NormalizeForSpeech(string? rawText, int maxChars, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                continue;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }

        text = sb.ToString().Trim();
        text = RemoveIncompatibleCharacters(text);
        if (maxChars > 0 && text.Length > maxChars)
        {
            text = text[..maxChars];
            truncated = true;
        }

        return text;
    }

    public static string DetectLanguageHint(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "en";

        var sampleLen = Math.Min(text.Length, 400);
        var cjk = 0;
        var letters = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            var ch = text[i];
            if (IsCjk(ch))
                cjk++;
            else if (char.IsLetter(ch))
                letters++;
        }

        return cjk > 0 && cjk >= letters * 0.2 ? "zh" : "en";
    }

    /// <summary>
    /// Builds a speak plan. Auto mode picks voice by language; manual never swaps voice.
    /// </summary>
    public static SpeakPlan CreateSpeakPlan(string? raw, string? voiceOverride, double rate, int maxChars)
    {
        var text = NormalizeForSpeech(raw, maxChars, out var truncated);
        var languageHint = DetectLanguageHint(text);
        var clampedRate = ClampRate(rate);

        if (!string.IsNullOrWhiteSpace(voiceOverride))
        {
            return new SpeakPlan(
                text,
                voiceOverride.Trim(),
                clampedRate,
                languageHint,
                SelectionManual,
                VoiceSourceUser,
                FallbackFrom: null,
                truncated);
        }

        var voice = languageHint == "zh" ? VoiceXiaoxiao : VoiceJenny;
        return new SpeakPlan(
            text,
            voice,
            clampedRate,
            languageHint,
            SelectionAuto,
            VoiceSourceAuto,
            FallbackFrom: null,
            truncated);
    }

    public static SpeakPlan WithFallbackVoice(SpeakPlan plan, string fallbackVoice) =>
        plan with
        {
            Voice = fallbackVoice,
            VoiceSource = VoiceSourceFallback,
            FallbackFrom = plan.Voice
        };

    public static string ResolveVoice(string text, string? voiceOverride) =>
        CreateSpeakPlan(text, voiceOverride, rate: 1.0, maxChars: 0).Voice;

    public static bool IsChineseVoice(string? voice) =>
        !string.IsNullOrWhiteSpace(voice)
        && (voice.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(voice, VoiceXiaoxiao, StringComparison.OrdinalIgnoreCase)
            || string.Equals(voice, VoiceYunxi, StringComparison.OrdinalIgnoreCase));

    public static string LocaleFromVoice(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
            return "en-US";
        if (voice.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        if (voice.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
            return "en-US";
        if (voice.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            return "en-US";
        if (voice.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        return "en-US";
    }

    public static double ClampRate(double rate) => Math.Clamp(rate, 0.5, 2.0);

    public static string FormatProsodyRate(double rate)
    {
        rate = ClampRate(rate);
        var percent = (int)Math.Round((rate - 1.0) * 100.0, MidpointRounding.AwayFromZero);
        return percent >= 0
            ? $"+{percent.ToString(CultureInfo.InvariantCulture)}%"
            : $"{percent.ToString(CultureInfo.InvariantCulture)}%";
    }

    public static string EscapeSsml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    public static string BuildSsml(string voice, string escapedText, double rate)
    {
        var prosodyRate = FormatProsodyRate(rate);
        var xmlLang = LocaleFromVoice(voice);
        return
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{xmlLang}'>" +
            $"<voice name='{voice}'>" +
            $"<prosody pitch='+0Hz' rate='{prosodyRate}' volume='+0%'>" +
            escapedText +
            "</prosody></voice></speak>";
    }

    private static bool IsCjk(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\uAC00' and <= '\uD7AF');

    private static string RemoveIncompatibleCharacters(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var code = (int)chars[i];
            if ((code >= 0 && code <= 8) || (code >= 11 && code <= 12) || (code >= 14 && code <= 31))
                chars[i] = ' ';
        }

        return new string(chars);
    }
}
