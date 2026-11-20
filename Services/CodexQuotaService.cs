using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LocalDeck.Services;

public sealed record CodexQuotaWindow(int UsedPercent, int WindowMinutes, long? ResetsAt);

public sealed record CodexQuotaResult(
    CodexQuotaWindow? Primary,
    CodexQuotaWindow? Secondary,
    string? PlanType,
    int? ResetCredits,
    string Source,
    DateTimeOffset ObservedAt,
    bool IsStale,
    string? Note);

public sealed class CodexQuotaException : Exception
{
    public CodexQuotaException(string message) : base(message) { }
}

/// <summary>
/// Read-only Codex quota reader.
///
/// Preferred path: the documented `codex app-server` JSON-RPC surface.
/// Fallback 1: the local HTTP usage endpoint using the current Codex access token.
/// Fallback 2: the most recent rate-limit snapshot in local Codex rollout logs.
/// Tokens are never refreshed, persisted, logged, or displayed.
/// </summary>
public sealed class CodexQuotaService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly string _codexHome;
    private readonly string _configuredPath;
    private static readonly string? DebugLogPath =
        Environment.GetEnvironmentVariable("LOCALDECK_CODEX_LOG");

    public CodexQuotaService(string? configuredPath = null)
    {
        _codexHome = AppPaths.CodexHome;
        _configuredPath = configuredPath ?? string.Empty;
    }

    public string CodexHome => _codexHome;
    public string LastDiagnostics { get; private set; } = string.Empty;

    private static void Debug(string message)
    {
        if (string.IsNullOrWhiteSpace(DebugLogPath)) return;
        try
        {
            File.AppendAllText(DebugLogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics are best-effort only.
        }
    }

    public async Task<CodexQuotaResult> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        var executable = FindCodexExecutable(_configuredPath);
        Debug($"executable={executable ?? "<none>"} configured={_configuredPath}");
        if (executable is not null)
        {
            try
            {
                var result = await ReadWithAppServerAsync(executable, cancellationToken);
                if (result is not null)
                {
                    LastDiagnostics = $"app-server OK · {result.Source}";
                    Debug("app-server success");
                    return result;
                }
                errors.Add("app-server 未返回数据");
                Debug("app-server returned no data");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add("app-server 响应超时");
                Debug("app-server timeout");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"app-server 不可用（{ex.GetType().Name}）");
                Debug($"app-server exception {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            errors.Add("未找到 Codex CLI");
        }

        try
        {
            var result = await ReadWithHttpAsync(cancellationToken);
            if (result is not null)
            {
                LastDiagnostics = $"HTTP OK · app-server: {string.Join(" / ", errors)}";
                return result;
            }
            errors.Add("额度接口不可用");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            errors.Add("额度接口超时");
        }
        catch (CodexQuotaException ex)
        {
            errors.Add(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"额度接口异常（{ex.GetType().Name}）");
        }

        try
        {
            var result = ReadFromRolloutLogs(cancellationToken);
            if (result is not null)
            {
                LastDiagnostics = $"JSONL OK · {string.Join(" / ", errors)}";
                return result;
            }
            errors.Add("没有本地额度记录");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add("本地记录不可读");
        }

        LastDiagnostics = string.Join(" / ", errors);
        var detail = errors.Distinct().Take(2).ToArray();
        throw new CodexQuotaException(detail.Length == 0
            ? "未能读取 Codex 额度。请确认 Codex 已登录。"
            : $"未能读取 Codex 额度：{string.Join(" / ", detail)}");
    }

    public string? FindCodexExecutable() => FindCodexExecutable(_configuredPath);

    public static string? FindCodexExecutable(string configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = AppPaths.CodexHome;
        candidates.Add(Path.Combine(codexHome, ".sandbox-bin", "codex.exe"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe"));
        candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "codex.exe"));
        candidates.Add(Path.Combine(local, "Programs", "Codex", "codex.exe"));

        try
        {
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            foreach (var dir in Directory.EnumerateDirectories(windowsApps, "OpenAI.Codex_*")
                         .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(dir, "app", "resources", "codex.exe"));
            }
        }
        catch
        {
            // WindowsApps is normally ACL-restricted; other candidates still apply.
        }

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(path.Trim(), "codex.exe"));
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        candidates.Add(Path.Combine(userProfile, ".local", "bin", "codex.exe"));

        return candidates
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"'))
            .FirstOrDefault(File.Exists);
    }

    private async Task<CodexQuotaResult?> ReadWithAppServerAsync(string executable,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "-s read-only -a never app-server",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            Debug("process.Start returned false");
            return null;
        }
        Debug($"app-server started pid={process.Id}");

        // Drain stderr so a chatty server cannot block on a full pipe.
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (stderr.Count < 40) stderr.Enqueue(line);
                }
            }
            catch
            {
                // Ignore.
            }
        }, CancellationToken.None);

        try
        {
            var init = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "local-deck", title = "LocalDeck", version = "1.0.0" },
                    capabilities = new { experimentalApi = true }
                }
            };
            var initialized = new
            {
                jsonrpc = "2.0",
                method = "initialized",
                @params = new { }
            };
            var account = new { jsonrpc = "2.0", id = 2, method = "account/read", @params = new { } };
            var rateLimits = new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "account/rateLimits/read",
                @params = new { excludeResetCreditDetails = true }
            };

            foreach (var message in new object[] { init, initialized, account, rateLimits })
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
            }
            await process.StandardInput.FlushAsync();
            Debug("requests written");

            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    Debug("stdout EOF");
                    break;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) ||
                        !idElement.TryGetInt32(out var id) || id != 3)
                    {
                        continue;
                    }

                    Debug($"rate-limit reply received error={root.TryGetProperty("error", out _)}");
                    if (root.TryGetProperty("error", out _)) return null;
                    if (!root.TryGetProperty("result", out var result)) return null;

                    var parsed = ParseAppServerResult(result, DateTimeOffset.Now);
                    if (parsed is not null) return parsed;
                    Debug("rate-limit reply did not parse");
                }
                catch (JsonException)
                {
                    // Notifications and non-JSON output are not our business.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Caller converts this into a friendly timeout note.
        }
        catch
        {
            // Fall through to the next source.
        }
        finally
        {
            Debug($"app-server ending exited={process.HasExited} code={(process.HasExited ? process.ExitCode : -1)} stderr_lines={stderr.Count}");
            TryKill(process);
        }

        return null;
    }

    private async Task<CodexQuotaResult?> ReadWithHttpAsync(CancellationToken cancellationToken)
    {
        var authPath = Path.Combine(_codexHome, "auth.json");
        if (!File.Exists(authPath)) throw new CodexQuotaException("未找到 Codex 登录信息");

        string accessToken;
        string? accountId;
        try
        {
            using var authStream = new FileStream(authPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = await JsonDocument.ParseAsync(authStream, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens)) throw new CodexQuotaException("Codex 登录信息不完整");
            if (!tokens.TryGetProperty("access_token", out var tokenElement)) throw new CodexQuotaException("Codex 登录信息不完整");
            accessToken = tokenElement.GetString() ?? string.Empty;
            accountId = tokens.TryGetProperty("account_id", out var accountElement)
                ? accountElement.GetString()
                : null;
        }
        catch (CodexQuotaException)
        {
            throw;
        }
        catch
        {
            throw new CodexQuotaException("Codex 登录信息不可读");
        }

        if (string.IsNullOrWhiteSpace(accessToken)) throw new CodexQuotaException("Codex 尚未登录");

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocalDeck/1.0");
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            attemptCts.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CodexQuotaException("Codex 登录已过期，请重新登录 Codex");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CodexQuotaException($"额度接口返回 {(int)response.StatusCode}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CodexQuotaException("额度接口返回了非 JSON 内容");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(attemptCts.Token);
        using var body = await JsonDocument.ParseAsync(responseStream, cancellationToken: attemptCts.Token);
        var parsed = ParseHttpResult(body.RootElement, DateTimeOffset.Now);
        return parsed ?? throw new CodexQuotaException("额度接口返回了未知格式");
    }

    private CodexQuotaResult? ReadFromRolloutLogs(CancellationToken cancellationToken)
    {
        var files = new List<FileInfo>();
        foreach (var baseDir in new[]
                 {
                     Path.Combine(_codexHome, "sessions"),
                     Path.Combine(_codexHome, "archived_sessions")
                 })
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                files.AddRange(new DirectoryInfo(baseDir)
                    .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories));
            }
            catch
            {
                // Ignore unreadable session trees.
            }
        }

        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = TryReadRolloutTail(file);
            if (result is not null) return result;
        }

        return null;
    }

    private static CodexQuotaResult? TryReadRolloutTail(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            const int maxTail = 4 * 1024 * 1024;
            var length = stream.Length;
            if (length <= 0) return null;
            var take = (int)Math.Min(maxTail, length);
            stream.Seek(-take, SeekOrigin.End);
            var bytes = new byte[take];
            var read = stream.Read(bytes, 0, take);
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            var lines = text.Split('\n');

            // The first line may be a partial fragment of a very large line.
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length < 20 || !line.Contains("\"rate_limits\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var parsed = ParseRolloutLine(line, DateTimeOffset.Now);
                if (parsed is not null) return parsed;
            }
        }
        catch
        {
            // Try the next file.
        }

        return null;
    }

    private static int? FindWindowMinutes(JsonElement window)
    {
        foreach (var name in new[] { "windowDurationMins", "window_minutes", "windowMinutes" })
        {
            if (window.TryGetProperty(name, out var element) && element.TryGetInt32(out var mins) && mins > 0)
            {
                return mins;
            }
        }

        foreach (var name in new[] { "limit_window_seconds", "windowSeconds" })
        {
            if (window.TryGetProperty(name, out var element) && element.TryGetInt64(out var seconds) && seconds > 0)
            {
                var minutes = Math.Round(seconds / 60d);
                return (int)Math.Clamp(minutes, 1, int.MaxValue);
            }
        }

        return null;
    }

    private static CodexQuotaWindow? ParseWindow(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var used = 0.0;
        var hasUsed = false;
        if (window.TryGetProperty("usedPercent", out var usedCamel) && usedCamel.TryGetDouble(out var camel))
        {
            used = camel;
            hasUsed = true;
        }
        else if (window.TryGetProperty("used_percent", out var usedSnake) && usedSnake.TryGetDouble(out var snake))
        {
            used = snake;
            hasUsed = true;
        }

        var minutes = FindWindowMinutes(window);
        long? resetAt = null;
        foreach (var name in new[] { "resetsAt", "resets_at", "reset_at" })
        {
            if (window.TryGetProperty(name, out var element) && element.TryGetInt64(out var epoch) && epoch > 0)
            {
                resetAt = epoch;
                break;
            }
        }

        // A partially renamed or empty object must not masquerade as a healthy 0 % window.
        if (!hasUsed && minutes is null && resetAt is null) return null;

        return new CodexQuotaWindow(
            (int)Math.Round(Math.Clamp(used, 0, 100), MidpointRounding.AwayFromZero),
            minutes ?? 0,
            resetAt);
    }

    private static CodexQuotaResult? BuildResult(CodexQuotaWindow? first, CodexQuotaWindow? second,
        string? planType, int? resetCredits, string source, DateTimeOffset observedAt, bool stale,
        string? note)
    {
        var windows = new[] { first, second }
            .Where(w => w is not null)
            .Select(w => w!)
            .OrderBy(w => w.WindowMinutes <= 0 ? int.MaxValue : w.WindowMinutes)
            .ToList();
        if (windows.Count == 0) return null;
        return new CodexQuotaResult(
            windows[0],
            windows.Count > 1 ? windows[1] : null,
            planType,
            resetCredits,
            source,
            observedAt,
            stale,
            note);
    }

    public static CodexQuotaResult? ParseAppServerResult(JsonElement result, DateTimeOffset observedAt)
    {
        try
        {
            JsonElement snapshot = default;
            if (result.TryGetProperty("rateLimitsByLimitId", out var byId) &&
                byId.ValueKind == JsonValueKind.Object && byId.EnumerateObject().Any())
            {
                if (byId.TryGetProperty("codex", out var codex)) snapshot = codex;
                else snapshot = byId.EnumerateObject().First().Value;
            }
            else if (result.TryGetProperty("rateLimits", out var single))
            {
                snapshot = single;
            }

            if (snapshot.ValueKind != JsonValueKind.Object) return null;

            var primary = ParseWindow(snapshot, "primary");
            var secondary = ParseWindow(snapshot, "secondary");
            var plan = snapshot.TryGetProperty("planType", out var p) ? p.GetString() : null;

            int? resetCredits = null;
            if (result.TryGetProperty("rateLimitResetCredits", out var credits) &&
                credits.ValueKind == JsonValueKind.Object &&
                credits.TryGetProperty("availableCount", out var count) &&
                count.TryGetInt32(out var c))
            {
                resetCredits = c;
            }

            var reached = snapshot.TryGetProperty("rateLimitReachedType", out var r) &&
                          r.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrEmpty(r.GetString());
            var note = reached ? "当前窗口已触发限额" : null;
            return BuildResult(primary, secondary, plan, resetCredits, "APP-SERVER", observedAt,
                false, note);
        }
        catch
        {
            return null;
        }
    }

    public static CodexQuotaResult? ParseHttpResult(JsonElement root, DateTimeOffset observedAt)
    {
        try
        {
            if (!root.TryGetProperty("rate_limit", out var rateLimit) ||
                rateLimit.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var primary = ParseWindow(rateLimit, "primary_window");
            var secondary = ParseWindow(rateLimit, "secondary_window");
            var plan = root.TryGetProperty("plan_type", out var p) ? p.GetString() : null;
            int? resetCredits = null;
            if (root.TryGetProperty("rate_limit_reset_credits", out var credits) &&
                credits.ValueKind == JsonValueKind.Object &&
                credits.TryGetProperty("available_count", out var count) &&
                count.TryGetInt32(out var c))
            {
                resetCredits = c;
            }

            var reached = rateLimit.TryGetProperty("limit_reached", out var reachedElement) &&
                          reachedElement.ValueKind == JsonValueKind.True;
            return BuildResult(primary, secondary, plan, resetCredits, "HTTP", observedAt, false,
                reached ? "当前窗口已触发限额" : null);
        }
        catch
        {
            return null;
        }
    }

    public static CodexQuotaResult? ParseRolloutLine(string line, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("rate_limits", out var rateLimits) ||
                rateLimits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!payload.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "token_count", StringComparison.Ordinal))
            {
                return null;
            }

            var primary = ParseWindow(rateLimits, "primary");
            var secondary = ParseWindow(rateLimits, "secondary");
            var plan = rateLimits.TryGetProperty("plan_type", out var p) ? p.GetString() : null;

            var timestamp = now;
            if (root.TryGetProperty("timestamp", out var ts) &&
                DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsedTime))
            {
                timestamp = parsedTime.ToLocalTime();
            }

            var stale = now - timestamp > TimeSpan.FromMinutes(15);
            return BuildResult(primary, secondary, plan, null, "本地记录", timestamp, stale,
                stale ? "本地快照，可能已过期" : null);
        }
        catch
        {
            return null;
        }
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

    public static string FormatReset(long? unixSeconds, DateTimeOffset now)
    {
        if (unixSeconds is null) return "重置时间未知";
        DateTimeOffset reset;
        try
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).ToLocalTime();
        }
        catch
        {
            return "重置时间未知";
        }

        var span = reset - now;
        if (span <= TimeSpan.Zero) return "等待新窗口";
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)} 分后重置";
        if (span.TotalDays < 1) return $"{span.Hours} 小时 {span.Minutes} 分后重置";
        if (span.TotalDays < 7) return $"{reset:M月d日 HH:mm} 重置";
        return reset.ToString("M月d日 HH:mm", CultureInfo.CurrentCulture);
    }

    public static string FormatWindowLabel(int minutes)
    {
        return minutes switch
        {
            300 => "5 小时",
            10080 => "7 天",
            0 => "额度窗口",
            _ when minutes % 1440 == 0 => $"{minutes / 1440} 天",
            _ when minutes % 60 == 0 => $"{minutes / 60} 小时",
            _ => $"{minutes} 分钟"
        };
    }
}
