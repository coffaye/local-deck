using System.IO;
using System.Text;
using System.Text.Json;
using LocalDeck.Services;

namespace LocalDeck;

/// <summary>
/// Headless verification used by the build pipeline. It exercises the parsers and
/// local hardware paths without making account changes.
/// </summary>
public static class SelfTest
{
    public static bool Run()
    {
        var lines = new List<string>();
        var failures = 0;

        void Check(string name, Func<bool> test)
        {
            try
            {
                var ok = test();
                lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                lines.Add($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        Check("DPAPI roundtrip", () =>
        {
            var secret = "sk-localdeck-selftest-0123456789";
            var protectedValue = Dpapi.Protect(secret);
            return protectedValue != secret && Dpapi.Unprotect(protectedValue) == secret;
        });

        Check("byte rate formatting", () =>
            SystemSampler.FormatRate(188).Contains("B/s") &&
            SystemSampler.FormatRate(1536).Contains("KB/s") &&
            SystemSampler.FormatRate(3 * 1024 * 1024).Contains("MB/s"));

        Check("Codex app-server parse", () =>
        {
            const string json = """
            {"id":3,"result":{"rateLimits":{"limitId":"codex","planType":"plus","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1795068206},"secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1795663497}},"rateLimitsByLimitId":{"codex":{"limitId":"codex","planType":"plus","primary":{"usedPercent":100,"windowDurationMins":300,"resetsAt":1795068206},"secondary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1795663497}}},"rateLimitResetCredits":{"availableCount":2}}}
            """;
            using var doc = JsonDocument.Parse(json);
            var parsed = CodexQuotaService.ParseAppServerResult(
                doc.RootElement.GetProperty("result"), DateTimeOffset.Now);
            return parsed is not null &&
                   parsed.Primary?.UsedPercent == 100 &&
                   parsed.Primary?.WindowMinutes == 300 &&
                   parsed.Secondary?.UsedPercent == 0 &&
                   parsed.ResetCredits == 2 &&
                   parsed.PlanType == "plus";
        });

        Check("Codex HTTP parse", () =>
        {
            const string json = """
            {"plan_type":"plus","rate_limit":{"allowed":false,"limit_reached":true,"primary_window":{"used_percent":100,"limit_window_seconds":18000,"reset_at":1795068206},"secondary_window":{"used_percent":0,"limit_window_seconds":604800,"reset_at":1795663497}},"rate_limit_reset_credits":{"available_count":2}}
            """;
            using var doc = JsonDocument.Parse(json);
            var parsed = CodexQuotaService.ParseHttpResult(doc.RootElement, DateTimeOffset.Now);
            return parsed is not null &&
                   parsed.Primary?.WindowMinutes == 300 &&
                   parsed.Secondary?.WindowMinutes == 10080 &&
                   parsed.ResetCredits == 2;
        });

        Check("Codex rollout parse", () =>
        {
            const string json = """
            {"timestamp":"2026-11-19T02:29:10.092Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":99.0,"window_minutes":300,"resets_at":1795068206},"secondary":{"used_percent":79.0,"window_minutes":10080,"resets_at":1795056891},"plan_type":"plus"}}}
            """;
            var parsed = CodexQuotaService.ParseRolloutLine(json, DateTimeOffset.Now);
            return parsed is not null &&
                   parsed.Primary?.UsedPercent == 99 &&
                   parsed.Secondary?.UsedPercent == 79 &&
                   parsed.Source == "本地记录";
        });

        Check("DeepSeek balance parse", () =>
        {
            const string json = """
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"12.48","granted_balance":"2.00","topped_up_balance":"10.48"}]}
            """;
            using var doc = JsonDocument.Parse(json);
            var parsed = DeepSeekService.ParseBalance(doc.RootElement, DateTimeOffset.Now);
            return parsed.TotalBalance == 12.48m &&
                   parsed.GrantedBalance == 2.00m &&
                   parsed.ToppedUpBalance == 10.48m &&
                   parsed.Currency == "CNY" &&
                   parsed.IsAvailable;
        });

        Check("Claude usage parse", () =>
        {
            const string json = """
            {"balance":76.18474267,"daily_usage":[{"date":"2026-11-26","actual_cost":0.8874935744}],"isValid":true,"mode":"unrestricted","remaining":76.18474267,"unit":"USD","usage":{"today":{"actual_cost":0.8874935744,"cost":5.91662383}}}
            """;
            using var doc = JsonDocument.Parse(json);
            var parsed = ClaudeQuotaService.ParseUsage(
                doc.RootElement, DateTimeOffset.Now, "https://api.aijws.com/v1/usage");
            return parsed.IsValid &&
                   parsed.Balance == 76.18474267m &&
                   parsed.TodayCost == 0.8874935744m &&
                   parsed.Currency == "USD";
        });

        Check("Claude config URL normalization", () =>
            ClaudeConfigReader.NormalizeQueryUrl("https://api.aijws.com") ==
                "https://api.aijws.com/v1/usage" &&
            ClaudeConfigReader.NormalizeQueryUrl("https://api.aijws.com/v1") ==
                "https://api.aijws.com/v1/usage");

        Check("Codex reset formatting", () =>
            CodexQuotaService.FormatReset(
                DateTimeOffset.Now.AddDays(6).AddHours(1).ToUnixTimeSeconds(), DateTimeOffset.Now) == "6天后" &&
            CodexQuotaService.FormatReset(
                DateTimeOffset.Now.AddMinutes(38).AddSeconds(30).ToUnixTimeSeconds(), DateTimeOffset.Now) == "38分后");

        Check("Gemini agy groups JSON parse", () =>
        {
            const string json = """
            {"planType":"google_ai_pro","response":{"groups":[{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-weekly","window":"7d","remainingFraction":1.0,"resetTime":"2026-12-04T10:00:00Z"}]},{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h","displayName":"Five Hour","window":"5h","remainingFraction":0.86,"resetTime":"2026-11-27T15:00:00Z"},{"bucketId":"gemini-weekly","displayName":"Weekly","window":"7d","remainingFraction":0.92,"resetTime":"2026-12-02T10:00:00Z"}]}]}}
            """;
            var parsed = GeminiQuotaService.ParseAgyOutput(json, DateTimeOffset.Now);
            return parsed is not null &&
                   parsed.PlanType == "PRO" &&
                   parsed.Primary?.RemainingPercent == 86 &&
                   parsed.Primary?.WindowMinutes == 300 &&
                   parsed.Secondary?.RemainingPercent == 92 &&
                   parsed.Secondary?.WindowMinutes == 10080;
        });

        Check("Gemini agy envelope text parse", () =>
        {
            const string json = """
            {"status":"SUCCESS","response":"Gemini Models\nFive Hour (5h): 74% remaining (resets in 2h 15m)\nWeekly (7d): 90% remaining (resets in 5d)"}
            """;
            var parsed = GeminiQuotaService.ParseAgyOutput(json, DateTimeOffset.Now);
            return parsed is not null &&
                   parsed.Primary?.RemainingPercent == 74 &&
                   parsed.Primary?.ResetsAt is not null &&
                   parsed.Secondary?.RemainingPercent == 90 &&
                   parsed.Secondary?.ResetsAt is not null;
        });

        Check("Gemini agy error envelope returns null", () =>
            GeminiQuotaService.ParseAgyOutput("""{"status":"ERROR","error":"Not authenticated"}""", DateTimeOffset.Now) is null);

        Check("memory sensor", () =>
        {
            using var sampler = new SystemSampler();
            var snapshot = sampler.Sample(null);
            lines.Add($"      CPU={snapshot.CpuPercent:0.0}%  MEM={snapshot.MemoryPercent:0.0}%  TEMP={snapshot.CpuTemperatureC?.ToString("0.0") ?? "n/a"}  iGPU={snapshot.IntegratedGpu.UsagePercent?.ToString("0.0") ?? "n/a"}  dGPU={snapshot.DiscreteGpu.UsagePercent?.ToString("0.0") ?? "n/a"}");
            return snapshot.TotalMemoryGb > 0 && snapshot.MemoryPercent >= 0 && snapshot.MemoryPercent <= 100;
        });

        lines.Add(failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures})");
        try
        {
            File.WriteAllLines(AppPaths.SelfTestFile, lines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "LocalDeck-selftest.txt"),
                    lines.Append($"LOG WRITE FAILED: {ex.Message}"), Encoding.UTF8);
            }
            catch
            {
                // The exit code still communicates failure.
            }

            return false;
        }

        return failures == 0;
    }
}
