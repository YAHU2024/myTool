namespace QuickTranslate.Models;

internal sealed record ProviderPreset(
    string DisplayName,
    string ApiBaseUrl,
    string ModelName);

internal static class ProviderPresetCatalog
{
    private static readonly IReadOnlyList<ProviderPreset> Presets = Array.AsReadOnly<ProviderPreset>(
    [
        new("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen3-8B"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.7-flash"),
        new("DeepSeek", "https://api.deepseek.com/v1", "deepseek-v4-flash"),
        new("OpenAI", "https://api.openai.com/v1", "gpt-5.4")
    ]);

    public static ProviderPreset Default => Presets[0];

    public static IReadOnlyList<ProviderPreset> All => Presets;
}
