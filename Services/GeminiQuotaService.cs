using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalDeck.Services;

public sealed record GeminiQuotaWindow(
    int RemainingPercent,
    int WindowMinutes,
    long? ResetsAt,
    string? Label = null);

public sealed record GeminiQuotaResult(
    GeminiQuotaWindow? Primary,
    GeminiQuotaWindow? Secondary,
    string? PlanType,
    string Source,
    DateTimeOffset ObservedAt,
    bool IsStale,
    string? Note);

public sealed class GeminiQuotaException : Exception
{
    public GeminiQuotaException(string message) : base(message) { }
}

/// <summary>
/// Read-only Antigravity / Gemini quota reader via the official local `agy` CLI.
/// Runs `/usage` in non-interactive mode without sending model prompts or reading/storing OAuth tokens.
/// </summary>
public sealed class GeminiQuotaService
{
    private readonly string _configuredPath;

    public GeminiQuotaService(string? configuredPath = null)
    {
        _configuredPath = configuredPath ?? string.Empty;
    }

    public string LastDiagnostics { get; private set; } = string.Empty;

    public string? FindAgyExecutable() => FindAgyExecutable(_configuredPath);

    public static string? FindAgyExecutable(string configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var trimmed = path.Trim();
                candidates.Add(Path.Combine(trimmed, "agy.exe"));
                candidates.Add(Path.Combine(trimmed, "agy.cmd"));
                candidates.Add(Path.Combine(trimmed, "agy.bat"));
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        candidates.Add(Path.Combine(userProfile, ".local", "bin", "agy.exe"));
        candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity", "bin", "agy.exe"));
        candidates.Add(Path.Combine(userProfile, ".antigravity", "bin", "agy.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Antigravity", "agy.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Antigravity", "bin", "agy.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Antigravity", "resources", "app", "bin", "agy.exe"));
        candidates.Add(Path.Combine(local, "Antigravity", "agy.exe"));
        candidates.Add(Path.Combine(local, "Google", "Antigravity", "agy.exe"));
        candidates.Add(Path.Combine(roaming, "npm", "agy.cmd"));
        candidates.Add(Path.Combine(programFiles, "Antigravity", "agy.exe"));

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"'))
            .FirstOrDefault(File.Exists);
    }

    public async Task<GeminiQuotaResult> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindAgyExecutable(_configuredPath);
        if (executable is null)
        {
            LastDiagnostics = "未检测到 agy CLI";
            throw new GeminiQuotaException("未检测到 agy CLI，请在设置中指定路径");
        }

        var output = await RunAgyUsageAsync(executable, cancellationToken);
        var parsed = ParseAgyOutput(output, DateTimeOffset.Now);
        if (parsed is null)
        {
            LastDiagnostics = "agy 返回了无法识别的额度数据";
            throw new GeminiQuotaException("agy 未返回可识别的额度数据");
        }

        LastDiagnostics = $"AGY-CLI OK · {parsed.Source}";
        return parsed;
    }

    private static async Task<string> RunAgyUsageAsync(string executable, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var isScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                       executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isScript ? "cmd.exe" : executable,
            Arguments = isScript
                ? $"/d /c \"\"{executable}\" -p /usage --output-format json --print-timeout 12s\""
                : "-p /usage --output-format json --print-timeout 12s",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new GeminiQuotaException("无法启动 agy CLI");
            }
        }
        catch (GeminiQuotaException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GeminiQuotaException($"启动 agy 失败（{ex.GetType().Name}）");
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }

            if (process.ExitCode != 0)
            {
                var err = (stderr ?? string.Empty).Trim();
                if (err.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("sign in", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GeminiQuotaException("agy 尚未登录，请先在终端运行 agy 登录");
                }

                throw new GeminiQuotaException($"agy 执行失败（退出码 {process.ExitCode}）");
            }

            throw new GeminiQuotaException("agy 未输出额度数据");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new GeminiQuotaException("agy 查询超时");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>
    /// Parses JSON or text output from `agy -p /usage --output-format json`.
    /// Supports both `groups[].buckets[]` schema (RetrieveUserQuotaSummary) and headless envelope formats.
    /// </summary>
    public static GeminiQuotaResult? ParseAgyOutput(string rawOutput, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return null;
        var trimmed = rawOutput.Trim();

        // Try direct JSON parse first, or extract the first/last JSON object line (NDJSON-safe).
        var jsonElement = TryParseJsonElement(trimmed);
        if (jsonElement is { } root)
        {
            using var disposable = root;
            var parsed = ParseJsonRoot(root.RootElement, observedAt);
            if (parsed is not null) return parsed;
        }

        return ParseTextOutput(trimmed, observedAt);
    }

    public static GeminiQuotaResult? ParseJsonRoot(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // If this is a headless envelope with error status and no quota payload, bail out.
        if (TryGetString(root, "status") is { } status &&
            status.Equals("ERROR", StringComparison.OrdinalIgnoreCase) &&
            !root.TryGetProperty("groups", out _) &&
            !root.TryGetProperty("buckets", out _))
        {
            return null;
        }

        // Unwrap `structured_output`, `response`, `data`, or `result` objects if present.
        var plan = ExtractPlan(root);

        if (root.TryGetProperty("structured_output", out var structured) &&
            structured.ValueKind == JsonValueKind.Object)
        {
            var res = ParseQuotaObject(structured, plan, observedAt);
            if (res is not null) return res;
        }

        if (root.TryGetProperty("response", out var responseProp))
        {
            if (responseProp.ValueKind == JsonValueKind.Object)
            {
                var res = ParseQuotaObject(responseProp, plan ?? ExtractPlan(responseProp), observedAt);
                if (res is not null) return res;
            }
            else if (responseProp.ValueKind == JsonValueKind.String)
            {
                var innerText = responseProp.GetString();
                if (!string.IsNullOrWhiteSpace(innerText))
                {
                    using var innerDoc = TryParseJsonElement(innerText.Trim());
                    if (innerDoc is not null)
                    {
                        var res = ParseJsonRoot(innerDoc.RootElement, observedAt);
                        if (res is not null) return res;
                    }

                    var textRes = ParseTextOutput(innerText, observedAt, plan);
                    if (textRes is not null) return textRes;
                }
            }
        }

        if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
        {
            var res = ParseQuotaObject(dataProp, plan ?? ExtractPlan(dataProp), observedAt);
            if (res is not null) return res;
        }

        if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.Object)
        {
            var res = ParseQuotaObject(resultProp, plan ?? ExtractPlan(resultProp), observedAt);
            if (res is not null) return res;
        }

        return ParseQuotaObject(root, plan, observedAt);
    }

    private static GeminiQuotaResult? ParseQuotaObject(JsonElement obj, string? inheritedPlan, DateTimeOffset observedAt)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        var plan = ExtractPlan(obj) ?? inheritedPlan;

        // Pattern 1: groups[] -> buckets[]
        if (obj.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var selectedGroup = SelectGeminiGroup(groups);
            if (selectedGroup is { } group &&
                group.TryGetProperty("buckets", out var groupBuckets) &&
                groupBuckets.ValueKind == JsonValueKind.Array)
            {
                var windows = ParseBucketArray(groupBuckets);
                if (windows.Count > 0)
                {
                    return BuildResultFromWindows(windows, plan, "AGY-CLI", observedAt);
                }
            }
        }

        // Pattern 2: top-level buckets[] or quotas[]
        foreach (var arrayName in new[] { "buckets", "quotas", "quotaBuckets", "windows" })
        {
            if (obj.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var windows = ParseBucketArray(arr);
                if (windows.Count > 0)
                {
                    return BuildResultFromWindows(windows, plan, "AGY-CLI", observedAt);
                }
            }
        }

        // Pattern 3: primary / secondary object directly
        if (obj.TryGetProperty("primary", out _) || obj.TryGetProperty("secondary", out _))
        {
            var primary = obj.TryGetProperty("primary", out var p) ? ParseSingleBucket(p) : null;
            var secondary = obj.TryGetProperty("secondary", out var s) ? ParseSingleBucket(s) : null;
            if (primary is not null || secondary is not null)
            {
                return BuildResult(primary, secondary, plan, "AGY-CLI", observedAt);
            }
        }

        return null;
    }

    private static JsonElement? SelectGeminiGroup(JsonElement groups)
    {
        JsonElement? firstWithBuckets = null;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            if (!group.TryGetProperty("buckets", out var buckets) ||
                buckets.ValueKind != JsonValueKind.Array ||
                buckets.GetArrayLength() == 0)
            {
                continue;
            }

            firstWithBuckets ??= group;

            var name = TryGetString(group, "displayName") ??
                       TryGetString(group, "name") ??
                       TryGetString(group, "groupId") ??
                       string.Empty;
            if (name.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                var id = TryGetString(bucket, "bucketId") ?? TryGetString(bucket, "modelId") ?? string.Empty;
                if (id.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }
        }

        return firstWithBuckets;
    }

    private static List<GeminiQuotaWindow> ParseBucketArray(JsonElement buckets)
    {
        var list = new List<GeminiQuotaWindow>();
        foreach (var item in buckets.EnumerateArray())
        {
            var window = ParseSingleBucket(item);
            if (window is not null)
            {
                list.Add(window);
            }
        }
        return list;
    }

    private static GeminiQuotaWindow? ParseSingleBucket(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        int? remainingPercent = null;
        if (TryGetDouble(item, "remainingFraction") is { } frac ||
            TryGetDouble(item, "remaining_fraction") is { } frac2 && (frac = frac2) >= 0)
        {
            remainingPercent = (int)Math.Round(Math.Clamp(frac, 0.0, 1.0) * 100.0, MidpointRounding.AwayFromZero);
        }
        else if (TryGetDouble(item, "remainingPercent") is { } remPct ||
                 TryGetDouble(item, "remaining_percent") is { } remPct2 && (remPct = remPct2) >= 0)
        {
            remainingPercent = (int)Math.Round(Math.Clamp(remPct, 0.0, 100.0), MidpointRounding.AwayFromZero);
        }
        else if (TryGetDouble(item, "usedPercent") is { } usedPct ||
                 TryGetDouble(item, "used_percent") is { } usedPct2 && (usedPct = usedPct2) >= 0)
        {
            remainingPercent = 100 - (int)Math.Round(Math.Clamp(usedPct, 0.0, 100.0), MidpointRounding.AwayFromZero);
        }

        if (remainingPercent is null) return null;

        var windowMinutes = ParseWindowMinutes(item);
        var resetsAt = ParseResetUnixSeconds(item);
        var label = windowMinutes > 0
            ? CodexQuotaService.FormatWindowLabel(windowMinutes)
            : FormatBucketLabel(item);

        return new GeminiQuotaWindow(Math.Clamp(remainingPercent.Value, 0, 100), windowMinutes, resetsAt, label);
    }

    private static int ParseWindowMinutes(JsonElement item)
    {
        if (TryGetInt(item, "windowMinutes") is { } wm && wm > 0) return wm;
        if (TryGetInt(item, "windowDurationMins") is { } wdm && wdm > 0) return wdm;
        if (TryGetInt(item, "window_minutes") is { } wms && wms > 0) return wms;
        if (TryGetInt(item, "limit_window_seconds") is { } secs && secs > 0) return Math.Max(1, secs / 60);

        var hint = string.Join(" ",
            TryGetString(item, "window") ?? string.Empty,
            TryGetString(item, "bucketId") ?? string.Empty,
            TryGetString(item, "displayName") ?? string.Empty,
            TryGetString(item, "name") ?? string.Empty).ToLowerInvariant();

        if (hint.Contains("5h") || hint.Contains("5 hour") || hint.Contains("five hour") || hint.Contains("5小时"))
            return 300;
        if (hint.Contains("7d") || hint.Contains("week") || hint.Contains("7 day") || hint.Contains("7天") || hint.Contains("周"))
            return 10080;
        if (hint.Contains("24h") || hint.Contains("daily") || hint.Contains("1d") || hint.Contains("day"))
            return 1440;

        var match = Regex.Match(hint, @"\b(\d+)\s*([hdm])\b");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var num) && num > 0)
        {
            return match.Groups[2].Value switch
            {
                "h" => num * 60,
                "d" => num * 1440,
                "m" => num,
                _ => 0
            };
        }

        return 0;
    }

    private static string? FormatBucketLabel(JsonElement item)
    {
        var raw = TryGetString(item, "displayName") ?? TryGetString(item, "window") ?? TryGetString(item, "bucketId");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim();
    }

    private static long? ParseResetUnixSeconds(JsonElement item)
    {
        foreach (var key in new[] { "resetTime", "reset_time", "resetsAt", "resets_at", "reset_at" })
        {
            if (!item.TryGetProperty(key, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var unix))
            {
                return unix > 1_000_000_000_000L ? unix / 1000L : unix;
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUnix))
                {
                    return parsedUnix > 1_000_000_000_000L ? parsedUnix / 1000L : parsedUnix;
                }
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                {
                    return dto.ToUnixTimeSeconds();
                }
            }
        }
        return null;
    }

    private static GeminiQuotaResult? ParseTextOutput(string text, DateTimeOffset observedAt, string? plan = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Support lines like:
        // "Gemini (5h): 86% remaining (resets in 2h 14m)"
        // "Five Hour: 86% · resets in 2h 14m"
        // "Weekly (7d): 92% · resets in 5d"
        GeminiQuotaWindow? primary = null;
        GeminiQuotaWindow? secondary = null;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var pctMatch = Regex.Match(line, @"(\d{1,3})\s*%");
            if (!pctMatch.Success || !int.TryParse(pctMatch.Groups[1].Value, out var pct)) continue;

            var remaining = line.Contains("used", StringComparison.OrdinalIgnoreCase) &&
                            !line.Contains("remaining", StringComparison.OrdinalIgnoreCase) &&
                            !line.Contains("left", StringComparison.OrdinalIgnoreCase)
                ? 100 - Math.Clamp(pct, 0, 100)
                : Math.Clamp(pct, 0, 100);

            var resetUnix = ParseRelativeReset(line, observedAt);
            var lower = line.ToLowerInvariant();
            if (lower.Contains("5h") || lower.Contains("5 hour") || lower.Contains("five hour") || lower.Contains("5小时"))
            {
                primary ??= new GeminiQuotaWindow(remaining, 300, resetUnix, "5 小时");
            }
            else if (lower.Contains("7d") || lower.Contains("week") || lower.Contains("7 day") || lower.Contains("7天"))
            {
                secondary ??= new GeminiQuotaWindow(remaining, 10080, resetUnix, "7 天");
            }
            else if (primary is null)
            {
                primary = new GeminiQuotaWindow(remaining, 300, resetUnix, "5 小时");
            }
            else if (secondary is null)
            {
                secondary = new GeminiQuotaWindow(remaining, 10080, resetUnix, "7 天");
            }
        }

        if (primary is null && secondary is null) return null;
        return BuildResult(primary, secondary, plan, "AGY-CLI", observedAt);
    }

    private static long? ParseRelativeReset(string line, DateTimeOffset now)
    {
        var isoMatch = Regex.Match(line, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})");
        if (isoMatch.Success && DateTimeOffset.TryParse(isoMatch.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto.ToUnixTimeSeconds();
        }

        var dMatch = Regex.Match(line, @"(\d+)\s*(?:d|days?|天)", RegexOptions.IgnoreCase);
        var hMatch = Regex.Match(line, @"(\d+)\s*(?:h|hr|hrs|hours?|小时|时)", RegexOptions.IgnoreCase);
        var mMatch = Regex.Match(line, @"(\d+)\s*(?:m|min|mins|minutes?|分钟|分)", RegexOptions.IgnoreCase);

        if (!dMatch.Success && !hMatch.Success && !mMatch.Success) return null;

        var days = dMatch.Success ? int.Parse(dMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var hours = hMatch.Success ? int.Parse(hMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var mins = mMatch.Success ? int.Parse(mMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (days == 0 && hours == 0 && mins == 0) return null;

        return now.Add(new TimeSpan(days, hours, mins, 0)).ToUnixTimeSeconds();
    }

    private static GeminiQuotaResult BuildResultFromWindows(
        List<GeminiQuotaWindow> windows, string? plan, string source, DateTimeOffset observedAt)
    {
        var ordered = windows
            .OrderBy(w => w.WindowMinutes > 0 ? w.WindowMinutes : int.MaxValue)
            .ToList();
        var primary = ordered.ElementAtOrDefault(0);
        var secondary = ordered.ElementAtOrDefault(1);
        return BuildResult(primary, secondary, plan, source, observedAt);
    }

    private static GeminiQuotaResult BuildResult(
        GeminiQuotaWindow? primary, GeminiQuotaWindow? secondary, string? plan, string source, DateTimeOffset observedAt)
    {
        var normalizedPlan = NormalizePlan(plan);
        return new GeminiQuotaResult(primary, secondary, normalizedPlan, source, observedAt, false, null);
    }

    private static string? ExtractPlan(JsonElement obj)
    {
        return TryGetString(obj, "planType") ??
               TryGetString(obj, "plan_type") ??
               TryGetString(obj, "plan") ??
               TryGetString(obj, "tier");
    }

    private static string? NormalizePlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Contains("ultra", StringComparison.OrdinalIgnoreCase)) return "ULTRA";
        if (s.Contains("pro", StringComparison.OrdinalIgnoreCase)) return "PRO";
        if (s.Contains("plus", StringComparison.OrdinalIgnoreCase)) return "PLUS";
        if (s.Contains("enterprise", StringComparison.OrdinalIgnoreCase)) return "ENT";
        if (s.Contains("free", StringComparison.OrdinalIgnoreCase)) return "FREE";
        return s.Length <= 8 ? s.ToUpperInvariant() : s[..8].ToUpperInvariant();
    }

    private static JsonDocument? TryParseJsonElement(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            // Try parsing the last JSON object line in case diagnostics preceded JSON stdout.
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith('{') || !line.EndsWith('}')) continue;
                try
                {
                    return JsonDocument.Parse(line);
                }
                catch
                {
                    // Continue searching.
                }
            }
            return null;
        }
    }

    private static string? TryGetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static double? TryGetDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var n)) return n;
        if (val.ValueKind == JsonValueKind.String &&
            double.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static int? TryGetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var val)) return null;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n)) return n;
        if (val.ValueKind == JsonValueKind.String &&
            int.TryParse(val.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
