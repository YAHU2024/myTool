namespace QuickTranslate.Services;

internal enum ProviderKind
{
    Unknown,
    BigModel,
    DeepSeek,
    SiliconFlow,
    OpenAI
}

internal static class ProviderEndpointResolver
{
    public static ProviderKind Resolve(string apiBaseUrl)
    {
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri))
            return ProviderKind.Unknown;

        return uri.IdnHost.ToLowerInvariant() switch
        {
            "open.bigmodel.cn" => ProviderKind.BigModel,
            "api.deepseek.com" => ProviderKind.DeepSeek,
            "api.siliconflow.cn" => ProviderKind.SiliconFlow,
            "api.openai.com" => ProviderKind.OpenAI,
            _ => ProviderKind.Unknown
        };
    }
}
