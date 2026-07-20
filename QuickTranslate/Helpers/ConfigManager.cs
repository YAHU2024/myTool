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
                        return settings;
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
                Logger.Warn("ConfigManager", $"保存配置失败: {ex.Message}");
            }
        }
    }
}
