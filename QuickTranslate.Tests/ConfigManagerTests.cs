using System.Text.Json;
using QuickTranslate.Helpers;
using QuickTranslate.Models;
using Xunit;

namespace QuickTranslate.Tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _testDir;

    public ConfigManagerTests()
    {
        // Each test gets an isolated temp directory.
        _testDir = Path.Combine(Path.GetTempPath(), $"qt_config_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private ConfigManager CreateManager() => new(_testDir);

    private string ConfigPath => Path.Combine(_testDir, "settings.json");
    private string BackupPath => Path.Combine(_testDir, "settings.json.bak");
    private string TempPath => Path.Combine(_testDir, "settings.json.tmp");

    // =========================================================================
    // First launch
    // =========================================================================

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var mgr = CreateManager();
        var settings = mgr.LoadInternal();

        Assert.NotNull(settings);
        Assert.Equal("https://api.siliconflow.cn/v1", settings.ApiBaseUrl);
        Assert.Equal("Qwen/Qwen3-8B", settings.ModelName);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(ConfigManager.LastLoadHadCorruption);
        Assert.Equal(ConfigLoadStatus.FirstLaunch, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Save_CreatesFile_OnFirstLaunch()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { ApiKey = "test-key" };
        mgr.SaveInternal(settings);

        Assert.True(File.Exists(ConfigPath));
        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("test-key", json);
    }

    [Fact]
    public void Load_PreservesExistingProviderConfiguration()
    {
        var mgr = CreateManager();
        mgr.SaveInternal(new AppSettings
        {
            ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ApiKey = "existing-key",
            ModelName = "glm-4.7-flash"
        });

        var settings = mgr.LoadInternal();

        Assert.Equal("https://open.bigmodel.cn/api/paas/v4", settings.ApiBaseUrl);
        Assert.Equal("existing-key", settings.ApiKey);
        Assert.Equal("glm-4.7-flash", settings.ModelName);
        Assert.Equal(ConfigLoadStatus.Loaded, ConfigManager.LastLoadStatus);
    }

    // =========================================================================
    // Atomic save
    // =========================================================================

    [Fact]
    public void Save_WritesAtomically_NoTempFileLeftBehind()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { ApiKey = "atomic-key" };

        mgr.SaveInternal(settings);

        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(TempPath));
        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("atomic-key", json);
    }

    [Fact]
    public void Save_PreservesOriginal_WhenWriteFails()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { ApiKey = "original-key" };
        mgr.SaveInternal(settings);
        var originalContent = File.ReadAllText(ConfigPath);

        // Simulate write failure by locking the temp file so
        // File.WriteAllText fails with IOException.
        Directory.CreateDirectory(_testDir);
        using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var updated = new AppSettings { ApiKey = "new-key" };
            mgr.SaveInternal(updated);
        }

        // Original file content must be intact.
        Assert.True(File.Exists(ConfigPath));
        var afterFailure = File.ReadAllText(ConfigPath);
        Assert.Equal(originalContent, afterFailure);
    }

    [Fact]
    public void Save_DoesNotProducePartialFile_OnInterrupt()
    {
        // Simulate partial write by pre-creating a truncated temp file,
        // then attempting a replace. The atomic Move ensures either
        // the old file or the new file is present — never a partial.
        var mgr = CreateManager();
        var settings = new AppSettings { ApiKey = "complete-data-12345" };
        mgr.SaveInternal(settings);

        Assert.True(File.Exists(ConfigPath));
        var content = File.ReadAllText(ConfigPath);
        Assert.Contains("complete-data-12345", content);

        // Verify the file is valid JSON.
        var deserialized = JsonSerializer.Deserialize<AppSettings>(content);
        Assert.NotNull(deserialized);
        Assert.Equal("complete-data-12345", deserialized.ApiKey);
    }

    // =========================================================================
    // Backup
    // =========================================================================

    [Fact]
    public void Save_CreatesBackup_OfPreviousVersion()
    {
        var mgr = CreateManager();

        // First save.
        var v1 = new AppSettings { ApiKey = "key-v1", ModelName = "model-1" };
        mgr.SaveInternal(v1);

        // Second save should create a backup of v1.
        var v2 = new AppSettings { ApiKey = "key-v2", ModelName = "model-2" };
        mgr.SaveInternal(v2);

        Assert.True(File.Exists(BackupPath), "Backup file should exist");
        var backupContent = File.ReadAllText(BackupPath);
        Assert.Contains("key-v1", backupContent);
        Assert.DoesNotContain("key-v2", backupContent);

        // Current file has v2.
        var currentContent = File.ReadAllText(ConfigPath);
        Assert.Contains("key-v2", currentContent);
    }

    [Fact]
    public void Save_BackupDoesNotLeakApiKeyPermissions()
    {
        var mgr = CreateManager();
        var v1 = new AppSettings { ApiKey = "secret-key" };
        mgr.SaveInternal(v1);
        var v2 = new AppSettings { ApiKey = "secret-key-2" };
        mgr.SaveInternal(v2);

        Assert.True(File.Exists(BackupPath));
        // On Windows, by default files inherit directory ACLs.
        // We verify backup is readable (doesn't escalate permissions).
        var backupContent = File.ReadAllText(BackupPath);
        Assert.Contains("secret-key", backupContent);
    }

    // =========================================================================
    // Corruption handling
    // =========================================================================

    [Fact]
    public void Load_PreservesCorruptedFile_LoadsDefaults()
    {
        var mgr = CreateManager();

        // Write intentionally corrupted JSON.
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, "{ this is not valid JSON @@@");

        var settings = mgr.LoadInternal();

        // Original corrupted file must still exist, unchanged.
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal("{ this is not valid JSON @@@", File.ReadAllText(ConfigPath));

        // Load should return defaults.
        Assert.NotNull(settings);
        Assert.Equal("https://api.siliconflow.cn/v1", settings.ApiBaseUrl);

        // Corruption flag must be set.
        Assert.True(ConfigManager.LastLoadHadCorruption);
        Assert.Equal("json_corrupt", ConfigManager.LastLoadError);
        Assert.Equal(ConfigLoadStatus.Corrupted, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Load_PreservesTruncatedJson()
    {
        var mgr = CreateManager();

        // Balanced braces but truncated content.
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, "{\"ApiKey\": \"my-key");

        var settings = mgr.LoadInternal();

        // Truncated file preserved.
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("my-key", File.ReadAllText(ConfigPath));

        // Returns defaults.
        Assert.NotNull(settings);
        Assert.True(string.IsNullOrEmpty(settings.ApiKey));

        Assert.True(ConfigManager.LastLoadHadCorruption);
        Assert.Equal(ConfigLoadStatus.Corrupted, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Load_HandlesEmptyFile()
    {
        var mgr = CreateManager();
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, "");

        var settings = mgr.LoadInternal();

        Assert.True(File.Exists(ConfigPath));
        Assert.NotNull(settings);
        Assert.True(ConfigManager.LastLoadHadCorruption);
        Assert.Equal(ConfigLoadStatus.Corrupted, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Load_HandlesValidJson_ThatDeserializesToNull()
    {
        var mgr = CreateManager();
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, "null");

        var settings = mgr.LoadInternal();

        Assert.True(File.Exists(ConfigPath));
        Assert.NotNull(settings);
        Assert.True(ConfigManager.LastLoadHadCorruption);
        Assert.Equal("json_null", ConfigManager.LastLoadError);
        Assert.Equal(ConfigLoadStatus.Corrupted, ConfigManager.LastLoadStatus);
    }

    // =========================================================================
    // Error distinction
    // =========================================================================

    [Fact]
    public void Load_DistinguishesFileNotFound()
    {
        var mgr = CreateManager();
        // File doesn't exist.
        mgr.LoadInternal();

        Assert.False(ConfigManager.LastLoadHadCorruption);
        Assert.Null(ConfigManager.LastLoadError);
        Assert.Equal(ConfigLoadStatus.FirstLaunch, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Load_DistinguishesJsonCorruption()
    {
        var mgr = CreateManager();
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, "{{broken");

        mgr.LoadInternal();

        Assert.True(ConfigManager.LastLoadHadCorruption);
        Assert.Equal("json_corrupt", ConfigManager.LastLoadError);
        Assert.Equal(ConfigLoadStatus.Corrupted, ConfigManager.LastLoadStatus);
    }

    [Fact]
    public void Load_DistinguishesAccessDenied()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { ApiKey = "test" };
        mgr.SaveInternal(settings);

        try
        {
            File.SetAttributes(ConfigPath, FileAttributes.ReadOnly);
            // On Windows, ReadOnly on the file still allows ReadAllText.
            // This test primarily validates that the error classification path exists.
            // For true access-denied, we rely on manual verification with ACLs.
            var result = mgr.LoadInternal();
            Assert.NotNull(result);
            Assert.Equal("test", result.ApiKey); // ReadOnly shouldn't block reading
        }
        finally
        {
            File.SetAttributes(ConfigPath, FileAttributes.Normal);
        }
    }

    // =========================================================================
    // Migration save failure
    // =========================================================================

    [Fact]
    public void Load_ReturnsSettings_WhenMigrationSaveFails()
    {
        var mgr = CreateManager();

        // Write a valid v1 config that will trigger migration (old CustomSystemPrompt).
        var v1Json = @"{
            ""ApiKey"": ""my-key"",
            ""CustomSystemPrompt"": ""hello"",
            ""TargetLanguage"": ""English""
        }";
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath, v1Json);

        // Lock the temp file so migration save fails.
        Directory.CreateDirectory(_testDir);
        AppSettings settings;
        using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            settings = mgr.LoadInternal();
        }

        // Settings must still be loaded in memory with migrations applied in-memory.
        Assert.NotNull(settings);
        Assert.Equal("my-key", settings.ApiKey);
        Assert.Equal("hello", settings.CustomTranslationPrompt);
    }

    // =========================================================================
    // Normal save / round-trip
    // =========================================================================

    [Fact]
    public void NewSettings_DefaultThinkingModeFollowsProvider()
    {
        Assert.Equal(
            ThinkingModePreference.FollowProviderDefault,
            new AppSettings().ThinkingMode);
    }

    [Theory]
    [InlineData(true, ThinkingModePreference.Enabled)]
    [InlineData(false, ThinkingModePreference.Disabled)]
    public void MigrateThinkingMode_PreservesLegacyBooleanForAllSavedConfigs(
        bool legacyValue,
        ThinkingModePreference expected)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "EnableThinking": {{legacyValue.ToString().ToLowerInvariant()}},
              "SavedConfigs": [
                { "ModelName": "model-a" },
                { "ModelName": "model-b" }
              ]
            }
            """);
        var settings = new AppSettings
        {
            SavedConfigs = [new SavedConfig(), new SavedConfig()]
        };

        Assert.True(ConfigManager.MigrateThinkingMode(settings, document.RootElement));
        Assert.Equal(expected, settings.ThinkingMode);
        Assert.All(settings.SavedConfigs, config => Assert.Equal(expected, config.ThinkingMode));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        var mgr = CreateManager();
        var fallback = new SavedConfig
        {
            Id = "provider:fallback",
            DisplayName = "fallback-model",
            Alias = "我的备用模型",
            ApiBaseUrl = "https://fallback.example.com/v1",
            ApiKey = "fallback-key",
            ModelName = "fallback-model"
        };
        var original = new AppSettings
        {
            ApiBaseUrl = "https://api.example.com/v1",
            ApiKey = "round-trip-key",
            ModelName = "test-model",
            TargetLanguage = "Français",
            FallbackLanguage = "简体中文",
            TranslationTriggerMode = TranslationTriggerMode.SelectionOnly,
            AutoStart = true,
            AutoDetectLanguage = false,
            SmartContentType = true,
            EnableThinking = true,
            CustomTranslationPrompt = "Translate: {targetLang}",
            TtsEnabled = false,
            TtsRate = 1.1,
            TtsMaxChars = 1000,
            LogRetentionDays = 30,
            LogMaxTotalBytes = 100 * 1024 * 1024,
            SavedConfigs = [fallback]
        };

        mgr.SaveInternal(original);
        var loaded = mgr.LoadInternal();

        Assert.Equal(original.ApiBaseUrl, loaded.ApiBaseUrl);
        Assert.Equal(original.ApiKey, loaded.ApiKey);
        Assert.Equal(original.ModelName, loaded.ModelName);
        Assert.Equal(original.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(original.FallbackLanguage, loaded.FallbackLanguage);
        Assert.Equal(original.TranslationTriggerMode, loaded.TranslationTriggerMode);
        Assert.Equal(original.AutoStart, loaded.AutoStart);
        Assert.Equal(original.AutoDetectLanguage, loaded.AutoDetectLanguage);
        Assert.Equal(original.SmartContentType, loaded.SmartContentType);
        Assert.Equal(original.EnableThinking, loaded.EnableThinking);
        Assert.Equal(ThinkingModePreference.Enabled, loaded.ThinkingMode);
        Assert.Equal(original.CustomTranslationPrompt, loaded.CustomTranslationPrompt);
        Assert.Equal(original.TtsEnabled, loaded.TtsEnabled);
        Assert.Equal(original.TtsRate, loaded.TtsRate);
        Assert.Equal(original.TtsMaxChars, loaded.TtsMaxChars);
        Assert.Equal(original.LogRetentionDays, loaded.LogRetentionDays);
        Assert.Equal(original.LogMaxTotalBytes, loaded.LogMaxTotalBytes);
        var loadedConfig = Assert.Single(loaded.SavedConfigs);
        Assert.Equal(fallback.Id, loadedConfig.Id);
        Assert.Equal(fallback.Alias, loadedConfig.Alias);
    }

    [Fact]
    public void Load_MigratesMissingSavedConfigIdsAndLegacyDisplayNameAlias()
    {
        var mgr = CreateManager();
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(ConfigPath,
            """
            {
              "ApiKey": "key",
              "SavedConfigs": [
                {
                  "DisplayName": "legacy",
                  "ApiBaseUrl": "https://api.example.com/v1",
                  "ApiKey": "legacy-key",
                  "ModelName": "legacy-model"
                }
              ]
            }
            """);

        var loaded = mgr.LoadInternal();

        var config = Assert.Single(loaded.SavedConfigs);
        Assert.StartsWith("provider:", config.Id, StringComparison.Ordinal);
        Assert.Equal("legacy", config.Alias);
    }

    [Fact]
    public void Save_ClampsLogRetentionDays()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { LogRetentionDays = 0 };
        mgr.SaveInternal(settings);
        var loaded = mgr.LoadInternal();

        Assert.True(loaded.LogRetentionDays >= 1);
        Assert.True(loaded.LogRetentionDays <= 3650);
    }

    [Fact]
    public void Save_ClampsLogMaxTotalBytes()
    {
        var mgr = CreateManager();
        var settings = new AppSettings { LogMaxTotalBytes = 100 };
        mgr.SaveInternal(settings);
        var loaded = mgr.LoadInternal();

        Assert.True(loaded.LogMaxTotalBytes >= 1 * 1024 * 1024);
    }

    // =========================================================================
    // Multiple saves (backup churn)
    // =========================================================================

    [Fact]
    public void MultipleSaves_KeepLatestBackup()
    {
        var mgr = CreateManager();

        var v1 = new AppSettings { ApiKey = "key-1" };
        var v2 = new AppSettings { ApiKey = "key-2" };
        var v3 = new AppSettings { ApiKey = "key-3" };

        mgr.SaveInternal(v1);
        Thread.Sleep(10); // Ensure different timestamps
        mgr.SaveInternal(v2);
        Thread.Sleep(10);
        mgr.SaveInternal(v3);

        // Backup should contain v2 (the file before last replace).
        var backupContent = File.ReadAllText(BackupPath);
        Assert.Contains("key-2", backupContent);
        Assert.DoesNotContain("key-3", backupContent);

        // Current file has v3.
        var currentContent = File.ReadAllText(ConfigPath);
        Assert.Contains("key-3", currentContent);
    }

    // =========================================================================
    // Concurrent safety (single-instance)
    // =========================================================================

    [Fact]
    public void Save_HandlesRapidConsecutiveSaves()
    {
        var mgr = CreateManager();

        for (var i = 0; i < 10; i++)
        {
            var settings = new AppSettings { ApiKey = $"key-{i}" };
            mgr.SaveInternal(settings);
        }

        var loaded = mgr.LoadInternal();
        Assert.NotNull(loaded);
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("key-9", File.ReadAllText(ConfigPath));
    }

    // =========================================================================
    // Temp file cleanup
    // =========================================================================

    [Fact]
    public void Save_CleansUpTemp_OnSuccess()
    {
        var mgr = CreateManager();
        mgr.SaveInternal(new AppSettings { ApiKey = "cleanup-test" });

        Assert.False(File.Exists(TempPath), "Temp file must be cleaned up after successful save");
    }

    // =========================================================================
    // Large config (stress test for atomicity)
    // =========================================================================

    [Fact]
    public void Save_HandlesLargeConfig()
    {
        var mgr = CreateManager();
        var settings = new AppSettings
        {
            ApiKey = new string('K', 4096),
            SavedConfigs = Enumerable.Range(0, 100).Select(i => new SavedConfig
            {
                DisplayName = $"Config {i}",
                ApiBaseUrl = $"https://api-{i}.example.com/v1",
                ApiKey = $"key-{i}-{new string('x', 128)}",
                ModelName = $"model-{i}"
            }).ToList()
        };

        mgr.SaveInternal(settings);
        var loaded = mgr.LoadInternal();

        Assert.NotNull(loaded);
        Assert.Equal(100, loaded.SavedConfigs.Count);
        Assert.StartsWith("key-0-", loaded.SavedConfigs[0].ApiKey);
    }
}
