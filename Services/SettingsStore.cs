using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalDeck.Services;

public static class AppPaths
{
    private static readonly Lazy<string> DataDirectoryLazy = new(ResolveDataDirectory);

    public static string DataDirectory => DataDirectoryLazy.Value;

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LastBalanceFile => Path.Combine(DataDirectory, "last-balance.json");
    public static string SelfTestFile => Path.Combine(Environment.CurrentDirectory, "LocalDeck-selftest.txt");

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("LOCALDECK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(custom) && IsWritable(custom))
        {
            return custom;
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalDeck");
        var portable = Path.Combine(AppContext.BaseDirectory, "LocalDeck-data");

        // Existing portable data is authoritative. This app has historically
        // stored settings next to the exe in sandboxed/portable environments;
        // switching to Roaming just because a medium-integrity launch can now
        // write there would make the saved DPAPI key disappear.
        if (File.Exists(Path.Combine(portable, "settings.json")) && IsWritable(portable))
        {
            return portable;
        }

        if (IsWritable(roaming))
        {
            return roaming;
        }

        // Portable fallback for read-only/sandboxed environments or USB sticks.
        if (IsWritable(portable))
        {
            return portable;
        }

        var temp = Path.Combine(Path.GetTempPath(), "LocalDeck");
        if (IsWritable(temp))
        {
            return temp;
        }

        return roaming;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "localdeck");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string CodexHome
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : custom;
        }
    }

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LocalDeck.exe");
}

public sealed class AppSettings
{
    public string DeepSeekApiKeyProtected { get; set; } = string.Empty;

    [JsonIgnore]
    public string DeepSeekApiKey { get; set; } = string.Empty;

    public string ClaudeQueryUrl { get; set; } = string.Empty;
    public string ClaudeApiKeyProtected { get; set; } = string.Empty;

    [JsonIgnore]
    public string ClaudeApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool ApiKeyCameFromEnvironment { get; set; }

    public string CodexPath { get; set; } = string.Empty;
    public string NetworkAdapterId { get; set; } = string.Empty;
    public int SampleSeconds { get; set; } = 2;
    public int QuotaRefreshMinutes { get; set; } = 5;
    public int UiTheme { get; set; }
    public double WindowOpacity { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; }
    public bool StartWithWindows { get; set; }
    public bool HideTitleBarOnDeactivation { get; set; }

    [JsonIgnore]
    public bool LoadFailed { get; set; }

    [JsonIgnore]
    public bool DeepSeekConfigured => !string.IsNullOrWhiteSpace(DeepSeekApiKey);
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                settings = new AppSettings();
            }
            else
            {
                settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            settings = new AppSettings { LoadFailed = true };
        }

        if (!string.IsNullOrWhiteSpace(settings.DeepSeekApiKeyProtected))
        {
            try
            {
                settings.DeepSeekApiKey = Dpapi.Unprotect(settings.DeepSeekApiKeyProtected);
            }
            catch
            {
                settings.DeepSeekApiKey = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ClaudeApiKeyProtected))
        {
            try
            {
                settings.ClaudeApiKey = Dpapi.Unprotect(settings.ClaudeApiKeyProtected);
            }
            catch
            {
                settings.ClaudeApiKey = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.DeepSeekApiKey))
        {
            var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                settings.DeepSeekApiKey = envKey.Trim();
                settings.ApiKeyCameFromEnvironment = true;
            }
        }

        settings.SampleSeconds = Math.Clamp(settings.SampleSeconds, 1, 60);
        settings.QuotaRefreshMinutes = Math.Clamp(settings.QuotaRefreshMinutes, 1, 60);
        settings.UiTheme = Math.Clamp(settings.UiTheme, 0, 1);
        settings.WindowOpacity = Math.Clamp(settings.WindowOpacity, 0.35, 1.0);
        return settings;
    }

    /// <summary>
    /// Persists settings. Pass a new key to replace the stored credential, or null to leave it untouched.
    /// Passing an empty string removes the stored credential.
    /// </summary>
    public static void Save(AppSettings settings, string? replacementApiKey = null,
        string? replacementClaudeApiKey = null)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);

        if (replacementApiKey is not null)
        {
            if (string.IsNullOrWhiteSpace(replacementApiKey))
            {
                settings.DeepSeekApiKeyProtected = string.Empty;
                settings.DeepSeekApiKey = string.Empty;
                settings.ApiKeyCameFromEnvironment = false;
            }
            else
            {
                settings.DeepSeekApiKey = replacementApiKey.Trim();
                settings.DeepSeekApiKeyProtected = Dpapi.Protect(settings.DeepSeekApiKey);
                settings.ApiKeyCameFromEnvironment = false;
            }
        }

        if (replacementClaudeApiKey is not null)
        {
            if (string.IsNullOrWhiteSpace(replacementClaudeApiKey))
            {
                settings.ClaudeApiKeyProtected = string.Empty;
                settings.ClaudeApiKey = string.Empty;
            }
            else
            {
                settings.ClaudeApiKey = replacementClaudeApiKey.Trim();
                settings.ClaudeApiKeyProtected = Dpapi.Protect(settings.ClaudeApiKey);
            }
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = Path.Combine(AppPaths.DataDirectory,
            $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // A leftover temp file is harmless.
            }
        }
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalDeck";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\"");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Startup registration is a convenience; never fail app startup over it.
        }
    }
}
