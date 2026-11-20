using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LocalDeck.Services;

public sealed record DeepSeekBalanceResult(
    bool IsAvailable,
    string Currency,
    decimal TotalBalance,
    decimal GrantedBalance,
    decimal ToppedUpBalance,
    DateTimeOffset ObservedAt);

public sealed class DeepSeekBalanceException : Exception
{
    public DeepSeekBalanceException(string message) : base(message) { }
}

public sealed class DeepSeekService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public async Task<DeepSeekBalanceResult> GetBalanceAsync(string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new DeepSeekBalanceException("未配置 DeepSeek 密钥");
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.deepseek.com/user/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("LocalDeck/1.0");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                attemptCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekBalanceException("连接 DeepSeek 超时");
        }
        catch (HttpRequestException)
        {
            throw new DeepSeekBalanceException("无法连接 DeepSeek");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new DeepSeekBalanceException("密钥无效或无权限");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new DeepSeekBalanceException("请求过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new DeepSeekBalanceException($"DeepSeek 返回 {(int)response.StatusCode}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new DeepSeekBalanceException("DeepSeek 返回了非 JSON 内容");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: attemptCts.Token);
                return ParseBalance(doc.RootElement, DateTimeOffset.Now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DeepSeekBalanceException("连接 DeepSeek 超时");
            }
            catch (DeepSeekBalanceException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new DeepSeekBalanceException("DeepSeek 返回了未知格式");
            }
        }
    }

    public static DeepSeekBalanceResult ParseBalance(JsonElement root, DateTimeOffset observedAt)
    {
        if (!root.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array ||
            infos.GetArrayLength() == 0)
        {
            throw new DeepSeekBalanceException("DeepSeek 未返回余额");
        }

        JsonElement selected = default;
        var found = false;
        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object) continue;
            var currency = info.TryGetProperty("currency", out var c) ? c.GetString() : null;
            if (string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                selected = info;
                found = true;
                break;
            }

            if (!found)
            {
                selected = info;
                found = true;
            }
        }

        if (!found)
        {
            throw new DeepSeekBalanceException("DeepSeek 未返回余额");
        }

        var currencyCode = selected.TryGetProperty("currency", out var currencyElement)
            ? currencyElement.GetString() ?? "CNY"
            : "CNY";
        var total = ParseDecimal(selected, "total_balance");
        var granted = ParseDecimal(selected, "granted_balance");
        var toppedUp = ParseDecimal(selected, "topped_up_balance");
        var available = root.TryGetProperty("is_available", out var availableElement)
            ? availableElement.ValueKind == JsonValueKind.True
            : total > 0;

        return new DeepSeekBalanceResult(available, currencyCode.ToUpperInvariant(), total,
            granted, toppedUp, observedAt);
    }

    private static decimal ParseDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var numeric)) return numeric;
        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        throw new DeepSeekBalanceException("DeepSeek 余额字段格式异常");
    }

    public static string CurrencySymbol(string currencyCode)
    {
        return currencyCode.ToUpperInvariant() switch
        {
            "CNY" => "¥",
            "USD" => "$",
            _ => currencyCode + " "
        };
    }

    public static string FormatAmount(decimal amount)
    {
        return amount.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
