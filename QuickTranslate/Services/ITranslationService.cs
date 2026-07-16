using System;
using System.Threading.Tasks;

namespace QuickTranslate.Services
{
    /// <summary>
    /// 翻译服务接口
    /// </summary>
    public interface ITranslationService
    {
        /// <summary>
        /// 流式翻译文本，通过回调函数逐步返回中间结果
        /// </summary>
        /// <param name="text">要翻译的文本</param>
        /// <param name="targetLang">目标语言</param>
        /// <param name="onChunk">每收到一段翻译内容时的回调</param>
        /// <returns>完整翻译结果</returns>
        Task<string> TranslateStreamingAsync(string text, string targetLang, Action<string> onChunk);

        /// <summary>
        /// 翻译文本（非流式，兼容旧逻辑）
        /// </summary>
        Task<string> TranslateAsync(string text, string targetLang);
    }
}
