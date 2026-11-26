using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalDeck.Services;

public sealed record ClaudeUsageResult(
    bool IsValid,
    decimal Balance,
    decimal TodayCost,
    string Currency,
    string Mode,
    DateTimeOffset ObservedAt,
    string QueryUrl);

public sealed class ClaudeQuotaException : Exception
{
    public ClaudeQuotaException(string message) : base(message)
    {
    }
}

public sealed record ClaudeResolvedConfig(
    bool HasToken,
    bool HasQueryUrl,
    string? Token,
    string? QueryUrl,
    string? BaseUrl,
    bool ConfigFileExists,
    string TokenSource,
    string QuerySource)
{
    public bool IsConfigured => HasToken && HasQueryUrl;
}

/// <summary>
/// Reads the Claude CLI settings and resolves the token / usage endpoint.
/// The token is kept in memory only; it is never logged or written to settings
/// unless the user explicitly saves an override.
/// </summary>
public static class ClaudeConfigReader
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static ClaudeResolvedConfig Resolve(AppSettings settings, bool ignoreSettings = false)
    {
        var raw = ReadRaw();
        var envToken = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"),
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var envBase = NullIfWhite(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"));
        var envQuery = FirstNonEmpty(
            Environment.GetEnvironmentVariable("CLAUDE_USAGE_URL"),
            Environment.GetEnvironmentVariable("ANTHROPIC_USAGE_URL"),
            Environment.GetEnvironmentVariable("ANTHROPIC_QUOTA_URL"));

        var token = ignoreSettings ? null : NullIfWhite(settings.ClaudeApiKey);
        var tokenSource = "设置";
        if (token is null)
        {
            token = envToken;
            tokenSource = "环境变量";
        }
        if (token is null)
        {
            token = raw.Token;
            tokenSource = "Claude CLI";
        }

        var baseUrl = envBase ?? raw.BaseUrl;
        var query = ignoreSettings ? null : NullIfWhite(settings.ClaudeQueryUrl);
        var querySource = "设置";
        if (query is null)
        {
            query = envQuery;
            querySource = "环境变量";
        }
        if (query is null)
        {
            query = raw.QueryUrl;
            querySource = "Claude CLI";
        }
        if (query is null && baseUrl is not null)
        {
            query = BuildUsageUrl(baseUrl);
            querySource = "Claude CLI Base URL";
        }

        query = NormalizeQueryUrl(query);
        return new ClaudeResolvedConfig(
            !string.IsNullOrWhiteSpace(token),
            !string.IsNullOrWhiteSpace(query),
            token,
            query,
            baseUrl,
            raw.FileExists,
            tokenSource,
            querySource);
    }

    public static string? DetectQueryUrl()
    {
        var raw = ReadRaw();
        var envBase = NullIfWhite(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"));
        var envQuery = FirstNonEmpty(
            Environment.GetEnvironmentVariable("CLAUDE_USAGE_URL"),
            Environment.GetEnvironmentVariable("ANTHROPIC_USAGE_URL"),
            Environment.GetEnvironmentVariable("ANTHROPIC_QUOTA_URL"));
        return NormalizeQueryUrl(envQuery ?? raw.QueryUrl ?? BuildUsageUrl(envBase ?? raw.BaseUrl));
    }

    public static bool ConfigExists => File.Exists(ConfigPath);

    private static RawConfig ReadRaw()
    {
        if (!File.Exists(ConfigPath))
        {
            return new RawConfig(null, null, null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (!document.RootElement.TryGetProperty("env", out var env) ||
                env.ValueKind != JsonValueKind.Object)
            {
                return new RawConfig(null, null, null, true);
            }

            return new RawConfig(
                FirstNonEmpty(GetString(env, "ANTHROPIC_AUTH_TOKEN"), GetString(env, "ANTHROPIC_API_KEY")),
                GetString(env, "ANTHROPIC_BASE_URL"),
                FirstNonEmpty(GetString(env, "CLAUDE_USAGE_URL"), GetString(env, "ANTHROPIC_USAGE_URL")),
                true);
        }
        catch
        {
            return new RawConfig(null, null, null, true);
        }
    }

    public static string? NormalizeQueryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var result = value.Trim().TrimEnd('/');
        if (uri.AbsolutePath is "" or "/")
        {
            return result + "/v1/usage";
        }
        if (result.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return result + "/usage";
        }
        return result;
    }

    private static string? BuildUsageUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var value = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }
        return value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value + "/usage"
            : value + "/v1/usage";
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfWhite(value.GetString())
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record RawConfig(string? Token, string? BaseUrl, string? QueryUrl, bool FileExists);
}

public sealed class ClaudeQuotaService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<ClaudeUsageResult> GetUsageAsync(
        ClaudeResolvedConfig config, CancellationToken cancellationToken)
    {
        if (!config.HasToken || string.IsNullOrWhiteSpace(config.Token))
        {
            throw new ClaudeQuotaException("未找到 Claude CLI 的 Key");
        }
        if (!config.HasQueryUrl || string.IsNullOrWhiteSpace(config.QueryUrl) ||
            !Uri.TryCreate(config.QueryUrl, UriKind.Absolute, out var uri))
        {
            throw new ClaudeQuotaException("Claude 查询地址无效");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "LocalDeck/1.6.0");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ClaudeQuotaException($"Claude 查询失败：{ex.GetType().Name}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var hint = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Key 无效或已过期"
                    : response.StatusCode == HttpStatusCode.Forbidden
                        ? "Key 无权访问"
                        : "查询地址或网络异常";
                throw new ClaudeQuotaException($"Claude 查询失败（{code}，{hint}）");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return ParseUsage(document.RootElement, DateTimeOffset.Now, config.QueryUrl!);
            }
            catch (JsonException)
            {
                throw new ClaudeQuotaException("Claude 查询返回了无法识别的数据");
            }
        }
    }

    public static ClaudeUsageResult ParseUsage(JsonElement root, DateTimeOffset observedAt, string queryUrl)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ClaudeQuotaException("Claude 查询返回格式不正确");
        }

        if (!root.TryGetProperty("balance", out _) &&
            !root.TryGetProperty("remaining", out _) &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }

        var balance = GetDecimal(root, "balance") ?? GetDecimal(root, "remaining");
        if (balance is null)
        {
            throw new ClaudeQuotaException("Claude 查询没有返回余额");
        }

        var unit = GetString(root, "unit") ?? "USD";
        var valid = GetBool(root, "isValid") ?? GetBool(root, "valid") ?? true;
        var mode = GetString(root, "mode") ?? string.Empty;
        var todayCost = GetDecimal(root, "actual_cost") ?? GetDecimal(root, "today_cost") ?? 0m;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("today", out var today) &&
            today.ValueKind == JsonValueKind.Object)
        {
            todayCost = GetDecimal(today, "actual_cost") ?? GetDecimal(today, "cost") ?? todayCost;
        }

        return new ClaudeUsageResult(
            valid,
            balance.Value,
            todayCost,
            unit.Trim().ToUpperInvariant(),
            mode,
            observedAt,
            queryUrl);
    }

    public static string CurrencySymbol(string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "USD" => "$",
            "CNY" or "RMB" => "¥",
            "EUR" => "€",
            "GBP" => "£",
            "JPY" => "¥",
            _ => currency
        };
    }

    public static string FormatBalance(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string FormatCost(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal? GetDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static bool? GetBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }
        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
