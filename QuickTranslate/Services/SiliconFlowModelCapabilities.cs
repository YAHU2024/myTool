namespace QuickTranslate.Services;

internal sealed record SiliconFlowModelCapabilities(
    bool SupportsThinking,
    string ThinkingParameterName = "enable_thinking");

internal static class SiliconFlowModelCapabilitiesResolver
{
    private static readonly HashSet<string> ThinkingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pro/zai-org/GLM-5",
        "Pro/zai-org/GLM-4.7",
        "deepseek-ai/DeepSeek-V3.2",
        "Pro/deepseek-ai/DeepSeek-V3.2",
        "zai-org/GLM-4.6",
        "Qwen/Qwen3-8B",
        "Qwen/Qwen3-14B",
        "Qwen/Qwen3-32B",
        "Qwen/Qwen3-30B-A3B",
        "tencent/Hunyuan-A13B-Instruct",
        "zai-org/GLM-4.5V",
        "deepseek-ai/DeepSeek-V3.1-Terminus",
        "Pro/deepseek-ai/DeepSeek-V3.1-Terminus",
        "Qwen/Qwen3.5-397B-A17B",
        "Qwen/Qwen3.5-122B-A10B",
        "Qwen/Qwen3.5-35B-A3B",
        "Qwen/Qwen3.5-27B",
        "Qwen/Qwen3.5-9B",
        "Qwen/Qwen3.5-4B"
    };

    private static readonly SiliconFlowModelCapabilities ThinkingModel = new(true);
    private static readonly SiliconFlowModelCapabilities NoThinkingModel = new(false);

    public static SiliconFlowModelCapabilities Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return NoThinkingModel;

        var model = modelName.Trim();
        if (ThinkingModels.Contains(model))
            return ThinkingModel;

        return NoThinkingModel;
    }
}
