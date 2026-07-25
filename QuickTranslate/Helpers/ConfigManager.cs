using System;
using System.IO;
using System.Text.Json;
using QuickTranslate.Models;
using QuickTranslate.Services;

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
                        using var document = JsonDocument.Parse(json);
                        var shouldSave = MigratePromptSettings(settings, document.RootElement);
                        shouldSave |= MigrateTranslationTriggerMode(settings, document.RootElement);
                        if (shouldSave)
                            Save(settings);
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
        internal static bool MigratePromptSettings(AppSettings settings, JsonElement root)
        {
            var changed = false;

            // 兼容旧版本共用的 CustomSystemPrompt，仅在新字段均未提供时迁移一次。
            if (root.TryGetProperty("CustomSystemPrompt", out var legacyPrompt) &&
                legacyPrompt.ValueKind == JsonValueKind.String &&
                !root.TryGetProperty("CustomTranslationPrompt", out _) &&
                !root.TryGetProperty("CustomAnalysisPrompt", out _))
            {
                var prompt = legacyPrompt.GetString() ?? string.Empty;
                settings.CustomTranslationPrompt = prompt;
                settings.CustomAnalysisPrompt = prompt;
                changed = true;
            }

            settings.AnalysisPromptProfiles ??= new List<AnalysisPromptProfile>();
            if (!root.TryGetProperty("SelectedAnalysisPromptId", out _))
            {
                if (!string.IsNullOrWhiteSpace(settings.CustomAnalysisPrompt))
                {
                    var profile = new AnalysisPromptProfile
                    {
                        Id = $"custom:{Guid.NewGuid():N}",
                        Name = "原自定义解析",
                        Prompt = settings.CustomAnalysisPrompt
                    };
                    settings.AnalysisPromptProfiles.Add(profile);
                    settings.SelectedAnalysisPromptId = profile.Id;
                }
                else
                {
                    settings.SelectedAnalysisPromptId = settings.AnalysisPreset switch
                    {
                        "learner" => "builtin:learner",
                        "literary" => "builtin:literary",
                        "business" => "builtin:business",
                        _ => AnalysisPromptCatalog.GeneralId
                    };
                }

                changed = true;
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedAnalysisPromptId) ||
                (settings.SelectedAnalysisPromptId.StartsWith("custom:", StringComparison.Ordinal) &&
                 !settings.AnalysisPromptProfiles.Any(profile =>
                     string.Equals(profile.Id, settings.SelectedAnalysisPromptId, StringComparison.Ordinal))) ||
                (!settings.SelectedAnalysisPromptId.StartsWith("custom:", StringComparison.Ordinal) &&
                 !AnalysisPromptCatalog.IsBuiltIn(settings.SelectedAnalysisPromptId)))
            {
                settings.SelectedAnalysisPromptId = AnalysisPromptCatalog.GeneralId;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 将旧 TranslationEnabled/HotKeyEnabled 迁移为 TranslationTriggerMode，并规范化新字段。
        /// </summary>
        internal static bool MigrateTranslationTriggerMode(AppSettings settings, JsonElement root)
        {
            var changed = false;
            var hasMode = root.TryGetProperty("TranslationTriggerMode", out var modeElement);
            var hasLastActive = root.TryGetProperty("LastActiveTranslationTriggerMode", out var lastActiveElement);

            if (hasMode)
            {
                if (TryReadTriggerMode(modeElement, out var parsedMode))
                {
                    var normalized = TranslationTriggerModes.Normalize(parsedMode);
                    if (settings.TranslationTriggerMode != normalized)
                    {
                        settings.TranslationTriggerMode = normalized;
                        changed = true;
                    }
                }
                else
                {
                    settings.TranslationTriggerMode = TranslationTriggerMode.Both;
                    changed = true;
                }
            }
            else
            {
                var translationEnabled = ReadBoolProperty(root, "TranslationEnabled", defaultValue: true);
                var hotKeyEnabled = ReadBoolProperty(root, "HotKeyEnabled", defaultValue: true);
                var migrated = translationEnabled
                    ? (hotKeyEnabled ? TranslationTriggerMode.Both : TranslationTriggerMode.SelectionOnly)
                    : TranslationTriggerMode.Off;

                settings.TranslationTriggerMode = migrated;
                changed = true;

                if (!hasLastActive)
                {
                    var migratedLastActive = translationEnabled
                        ? migrated
                        : (hotKeyEnabled ? TranslationTriggerMode.Both : TranslationTriggerMode.SelectionOnly);
                    settings.LastActiveTranslationTriggerMode =
                        TranslationTriggerModes.NormalizeActive(migratedLastActive);
                }
            }

            if (hasLastActive)
            {
                if (TryReadTriggerMode(lastActiveElement, out var parsedLastActive))
                {
                    var normalizedLastActive = TranslationTriggerModes.NormalizeActive(parsedLastActive);
                    if (settings.LastActiveTranslationTriggerMode != normalizedLastActive)
                    {
                        settings.LastActiveTranslationTriggerMode = normalizedLastActive;
                        changed = true;
                    }
                }
                else
                {
                    settings.LastActiveTranslationTriggerMode =
                        settings.TranslationTriggerMode == TranslationTriggerMode.Off
                            ? TranslationTriggerMode.Both
                            : TranslationTriggerModes.NormalizeActive(settings.TranslationTriggerMode);
                    changed = true;
                }
            }
            else if (hasMode)
            {
                settings.LastActiveTranslationTriggerMode =
                    settings.TranslationTriggerMode == TranslationTriggerMode.Off
                        ? TranslationTriggerMode.Both
                        : TranslationTriggerModes.NormalizeActive(settings.TranslationTriggerMode);
                changed = true;
            }

            var finalLastActive = TranslationTriggerModes.NormalizeActive(settings.LastActiveTranslationTriggerMode);
            if (settings.LastActiveTranslationTriggerMode != finalLastActive)
            {
                settings.LastActiveTranslationTriggerMode = finalLastActive;
                changed = true;
            }

            var finalMode = TranslationTriggerModes.Normalize(settings.TranslationTriggerMode);
            if (settings.TranslationTriggerMode != finalMode)
            {
                settings.TranslationTriggerMode = finalMode;
                changed = true;
            }

            return changed;
        }

        private static bool ReadBoolProperty(JsonElement root, string name, bool defaultValue)
        {
            if (!root.TryGetProperty(name, out var element))
                return defaultValue;
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
                _ => defaultValue
            };
        }

        private static bool TryReadTriggerMode(JsonElement element, out TranslationTriggerMode mode)
        {
            mode = TranslationTriggerMode.Both;
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetInt32(out var number):
                    if (Enum.IsDefined(typeof(TranslationTriggerMode), number))
                    {
                        mode = (TranslationTriggerMode)number;
                        return true;
                    }
                    return false;
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (Enum.TryParse<TranslationTriggerMode>(text, ignoreCase: true, out var parsed) &&
                        TranslationTriggerModes.IsDefined(parsed))
                    {
                        mode = parsed;
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }
    }
}
