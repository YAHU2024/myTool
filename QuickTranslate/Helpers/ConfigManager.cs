using System;
using System.IO;
using System.Text.Json;
using QuickTranslate.Models;

namespace QuickTranslate.Helpers
{
    /// <summary>
    /// 配置管理器 - 负责读写应用配置
    /// </summary>
    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickTranslate");

        private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>
        /// 加载配置，若配置文件不存在则创建默认配置
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 3650);
                        settings.LogMaxTotalBytes = Math.Clamp(
                            settings.LogMaxTotalBytes,
                            1 * 1024 * 1024,
                            1024L * 1024 * 1024);
                        // 兼容旧版本共用的 CustomSystemPrompt，仅在新字段均未提供时迁移一次。
                        using var document = JsonDocument.Parse(json);
                        if (document.RootElement.TryGetProperty("CustomSystemPrompt", out var legacyPrompt) &&
                            legacyPrompt.ValueKind == JsonValueKind.String &&
                            !document.RootElement.TryGetProperty("CustomTranslationPrompt", out _) &&
                            !document.RootElement.TryGetProperty("CustomAnalysisPrompt", out _))
                        {
                            var prompt = legacyPrompt.GetString() ?? string.Empty;
                            settings.CustomTranslationPrompt = prompt;
                            settings.CustomAnalysisPrompt = prompt;
                            Save(settings);
                        }
                        return settings;
                    }
                }
            }
            catch
            {
                // 配置文件损坏，使用默认配置
            }

            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
        }

        /// <summary>
        /// 保存配置到本地 JSON 文件
        /// </summary>
        public static void Save(AppSettings settings)
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch (Exception ex)
            {
                Logger.Warn("ConfigManager", "config.save_failed", new { error_type = ex.GetType().Name });
            }
        }
    }
}
