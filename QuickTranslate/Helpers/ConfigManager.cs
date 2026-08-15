using System;
using System.IO;
using System.Text.Json;
using QuickTranslate.Core;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.Helpers
{
    public enum ConfigLoadStatus
    {
        Loaded,
        FirstLaunch,
        Corrupted
    }

    /// <summary>
    /// Configuration manager with atomic saves, corruption recovery,
    /// and injectable paths for testing.
    /// </summary>
    public class ConfigManager
    {
        private readonly string _configDir;
        private readonly string _configFilePath;
        private readonly string _tempFilePath;
        private readonly string _backupFilePath;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // ---- Static facade for production use ----

        private static readonly ConfigManager _instance = new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickTranslate"));

        /// <summary>
        /// Whether the last Load() detected a corrupted or unreadable config file.
        /// UI should check this after Load() to offer a recovery prompt.
        /// </summary>
        public static bool LastLoadHadCorruption { get; private set; }

        /// <summary>
        /// Error category from the most recent Load(), or null on success.
        /// </summary>
        public static string? LastLoadError { get; private set; }

        /// <summary>
        /// Outcome of the most recent Load() operation.
        /// </summary>
        public static ConfigLoadStatus LastLoadStatus { get; private set; } = ConfigLoadStatus.Loaded;

        /// <summary>
        /// Load configuration. On first launch or corruption, returns defaults
        /// without overwriting the original file.
        /// </summary>
        public static AppSettings Load() => _instance.LoadInternal();

        /// <summary>
        /// Save configuration atomically (write-to-temp then replace).
        /// Keeps one backup of the previous valid file.
        /// </summary>
        public static void Save(AppSettings settings) => _instance.SaveInternal(settings);

        // ---- Instance (testable) ----

        /// <summary>
        /// Production constructor — uses %APPDATA%/QuickTranslate.
        /// </summary>
        public ConfigManager() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickTranslate"))
        {
        }

        /// <summary>
        /// Injectable constructor for tests.
        /// </summary>
        internal ConfigManager(string configDir)
        {
            _configDir = configDir;
            _configFilePath = Path.Combine(_configDir, "settings.json");
            _tempFilePath = Path.Combine(_configDir, "settings.json.tmp");
            _backupFilePath = Path.Combine(_configDir, "settings.json.bak");
        }

        internal AppSettings LoadInternal()
        {
            LastLoadHadCorruption = false;
            LastLoadError = null;
            LastLoadStatus = ConfigLoadStatus.Loaded;

            if (!File.Exists(_configFilePath))
            {
                // First launch — return defaults; do not persist until first Save().
                LastLoadStatus = ConfigLoadStatus.FirstLaunch;
                return new AppSettings();
            }

            string json;
            try
            {
                json = File.ReadAllText(_configFilePath);
            }
            catch (UnauthorizedAccessException)
            {
                LastLoadHadCorruption = true;
                LastLoadError = "access_denied";
                LastLoadStatus = ConfigLoadStatus.Corrupted;
                Logger.Error("ConfigManager", "config.load_access_denied",
                    new { error_type = "UnauthorizedAccessException" });
                return new AppSettings();
            }
            catch (IOException ex)
            {
                LastLoadHadCorruption = true;
                LastLoadError = "io_error";
                LastLoadStatus = ConfigLoadStatus.Corrupted;
                Logger.Error("ConfigManager", "config.load_io_error",
                    new { error_type = ex.GetType().Name });
                return new AppSettings();
            }

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json);
            }
            catch (JsonException)
            {
                // Corrupted JSON — preserve original file, load defaults.
                LastLoadHadCorruption = true;
                LastLoadError = "json_corrupt";
                LastLoadStatus = ConfigLoadStatus.Corrupted;
                Logger.Error("ConfigManager", "config.json_corrupt",
                    new { error_type = "JsonException" });
                return new AppSettings();
            }

            if (settings is null)
            {
                // Valid JSON but deserialized to null.
                LastLoadHadCorruption = true;
                LastLoadError = "json_null";
                LastLoadStatus = ConfigLoadStatus.Corrupted;
                Logger.Error("ConfigManager", "config.deserialize_null",
                    new { error_type = "NullResult" });
                return new AppSettings();
            }

            // Clamp log limits.
            settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 3650);
            settings.LogMaxTotalBytes = Math.Clamp(
                settings.LogMaxTotalBytes,
                1 * 1024 * 1024,
                1024L * 1024 * 1024);

            // Run migrations.
            using var document = JsonDocument.Parse(json);
            var shouldSave = MigratePromptSettings(settings, document.RootElement);
            shouldSave |= MigrateTranslationTriggerMode(settings, document.RootElement);
            shouldSave |= MigrateSavedConfigs(settings, document.RootElement);

            if (shouldSave)
            {
                try
                {
                    SaveInternal(settings);
                }
                catch
                {
                    // Migration save failure must not discard the in-memory config
                    // that was already successfully read.
                    Logger.Warn("ConfigManager", "config.migration_save_failed",
                        new { error_type = "migration_persistence" });
                }
            }

            return settings;
        }

        internal void SaveInternal(AppSettings settings)
        {
            string json;
            try
            {
                json = JsonSerializer.Serialize(settings, JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Error("ConfigManager", "config.serialize_failed",
                    new { error_type = ex.GetType().Name });
                return;
            }

            try
            {
                if (!Directory.Exists(_configDir))
                    Directory.CreateDirectory(_configDir);

                // 1. Write JSON to a temporary file (full write + close before rename).
                File.WriteAllText(_tempFilePath, json);

                // 2. If original exists, copy it as a backup so the user can recover.
                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        File.Copy(_configFilePath, _backupFilePath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        // Backup is best-effort; log but do not fail the save.
                        Logger.Warn("ConfigManager", "config.backup_failed",
                            new { error_type = ex.GetType().Name });
                    }
                }

                // 3. Atomic replace (File.Move is atomic on the same volume).
                File.Move(_tempFilePath, _configFilePath, overwrite: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error("ConfigManager", "config.save_access_denied",
                    new { error_type = ex.GetType().Name });
                TryCleanupTemp();
            }
            catch (IOException ex)
            {
                Logger.Error("ConfigManager", "config.save_io_error",
                    new { error_type = ex.GetType().Name });
                TryCleanupTemp();
            }
            catch (Exception ex)
            {
                Logger.Error("ConfigManager", "config.save_failed",
                    new { error_type = ex.GetType().Name });
                TryCleanupTemp();
            }
        }

        private void TryCleanupTemp()
        {
            try
            {
                if (File.Exists(_tempFilePath))
                    File.Delete(_tempFilePath);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        // ---- Migrations (unchanged) ----

        internal static bool MigratePromptSettings(AppSettings settings, JsonElement root)
        {
            var changed = false;

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

        internal static bool MigrateSavedConfigs(AppSettings settings, JsonElement root)
        {
            var changed = false;
            settings.SavedConfigs ??= new List<SavedConfig>();
            var serializedConfigs = root.TryGetProperty("SavedConfigs", out var savedConfigsElement) &&
                                    savedConfigsElement.ValueKind == JsonValueKind.Array
                ? savedConfigsElement.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < settings.SavedConfigs.Count; index++)
            {
                var config = settings.SavedConfigs[index];
                var serializedIdPresent = index < serializedConfigs.Length &&
                                          serializedConfigs[index].TryGetProperty("Id", out var idElement) &&
                                          idElement.ValueKind == JsonValueKind.String &&
                                          !string.IsNullOrWhiteSpace(idElement.GetString());
                if (string.IsNullOrWhiteSpace(config.Id) || !usedIds.Add(config.Id))
                {
                    config.Id = $"provider:{Guid.NewGuid():N}";
                    usedIds.Add(config.Id);
                    changed = true;
                }
                else if (!serializedIdPresent)
                {
                    changed = true;
                }

                var normalizedAlias = ModelProfileCatalog.ResolveLegacyAlias(config);
                var serializedAliasPresent = index < serializedConfigs.Length &&
                                             serializedConfigs[index].TryGetProperty("Alias", out _);
                if (!string.Equals(config.Alias, normalizedAlias, StringComparison.Ordinal) ||
                    !serializedAliasPresent)
                {
                    config.Alias = normalizedAlias;
                    changed = true;
                }
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
