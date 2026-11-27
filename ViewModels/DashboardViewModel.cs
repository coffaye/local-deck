using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LocalDeck.Services;

namespace LocalDeck.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class UiBrushes
{
    public static Brush Lime { get; } = Frozen("#00F0FF");
    public static Brush LimeSoft { get; } = Frozen("#FF007F");
    public static Brush Orange { get; } = Frozen("#FF007F");
    public static Brush Amber { get; } = Frozen("#FFEF00");
    public static Brush Paper { get; } = Frozen("#F7F4FF");
    public static Brush Muted { get; } = Frozen("#A79FCC");
    public static Brush MutedDark { get; } = Frozen("#6C628F");
    public static Brush Dark { get; } = Frozen("#0A0A0F");
    public static Brush Line { get; } = Frozen("#2B0B52");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class CodexQuotaCardViewModel : ObservableObject
{
    private string _planText = "—";
    private string _statusText = "读取中";
    private string _primaryLabel = "5 小时";
    private string _primaryRemainingText = "—";
    private double _primaryRemaining;
    private string _primaryResetText = "等待同步";
    private string _secondaryLabel = "7 天";
    private string _secondaryRemainingText = "—";
    private double _secondaryRemaining;
    private string _secondaryResetText = "等待同步";
    private string _resetCreditsText = "—";
    private string _sourceText = "尚未同步";
    private string _hintText = "正在读取 Codex 额度";
    private bool _hasData;
    private bool _isUnavailable;
    private bool _isLoading = true;
    private Brush _statusBrush = UiBrushes.Muted;
    private Brush _primaryBrush = UiBrushes.Muted;
    private Brush _secondaryBrush = UiBrushes.Muted;
    private bool _hasSecondary = true;
    private string _noteText = string.Empty;

    public string PlanText { get => _planText; set => Set(ref _planText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string PrimaryLabel { get => _primaryLabel; set => Set(ref _primaryLabel, value); }
    public string PrimaryRemainingText { get => _primaryRemainingText; set => Set(ref _primaryRemainingText, value); }
    public double PrimaryRemaining { get => _primaryRemaining; set => Set(ref _primaryRemaining, value); }
    public string PrimaryResetText { get => _primaryResetText; set => Set(ref _primaryResetText, value); }
    public string SecondaryLabel { get => _secondaryLabel; set => Set(ref _secondaryLabel, value); }
    public string SecondaryRemainingText { get => _secondaryRemainingText; set => Set(ref _secondaryRemainingText, value); }
    public double SecondaryRemaining { get => _secondaryRemaining; set => Set(ref _secondaryRemaining, value); }
    public string SecondaryResetText { get => _secondaryResetText; set => Set(ref _secondaryResetText, value); }
    public string ResetCreditsText { get => _resetCreditsText; set => Set(ref _resetCreditsText, value); }
    public string SourceText { get => _sourceText; set => Set(ref _sourceText, value); }
    public string HintText { get => _hintText; set => Set(ref _hintText, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public bool IsUnavailable { get => _isUnavailable; set => Set(ref _isUnavailable, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public Brush PrimaryBrush { get => _primaryBrush; set => Set(ref _primaryBrush, value); }
    public Brush SecondaryBrush { get => _secondaryBrush; set => Set(ref _secondaryBrush, value); }
    public bool HasSecondary { get => _hasSecondary; set => Set(ref _hasSecondary, value); }
    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }

    public void BeginLoading()
    {
        IsLoading = true;
        HintText = HasData ? "正在刷新额度" : "正在读取 Codex 额度";
    }

    public void Apply(CodexQuotaResult result)
    {
        HasData = true;
        IsUnavailable = false;
        IsLoading = false;
        PlanText = string.IsNullOrWhiteSpace(result.PlanType)
            ? "CODEX"
            : result.PlanType.ToUpperInvariant();
        SourceText = (result.Source == "本地记录" ? "LOCAL SNAPSHOT · " : result.Source + " · ") +
                     result.ObservedAt.ToString("HH:mm", CultureInfo.CurrentCulture);
        NoteText = result.Note ?? (result.IsStale ? "本地快照，可能已过期" : "—");

        if (result.Primary is not null)
        {
            ApplyWindow(result.Primary, ref _primaryLabel, ref _primaryRemainingText,
                ref _primaryRemaining, ref _primaryResetText, ref _primaryBrush, nameof(PrimaryLabel),
                nameof(PrimaryRemainingText), nameof(PrimaryRemaining), nameof(PrimaryResetText),
                nameof(PrimaryBrush));
        }
        else
        {
            PrimaryRemainingText = "—";
            PrimaryRemaining = 0;
            PrimaryResetText = "暂无窗口";
            PrimaryBrush = UiBrushes.Muted;
        }

        if (result.Secondary is not null)
        {
            HasSecondary = true;
            ApplyWindow(result.Secondary, ref _secondaryLabel, ref _secondaryRemainingText,
                ref _secondaryRemaining, ref _secondaryResetText, ref _secondaryBrush, nameof(SecondaryLabel),
                nameof(SecondaryRemainingText), nameof(SecondaryRemaining), nameof(SecondaryResetText),
                nameof(SecondaryBrush));
        }
        else
        {
            HasSecondary = false;
            SecondaryLabel = "—";
            SecondaryRemainingText = "—";
            SecondaryRemaining = 0;
            SecondaryResetText = "暂无窗口";
            SecondaryBrush = UiBrushes.Muted;
        }

        var primaryUsed = result.Primary?.UsedPercent ?? 0;
        var lowestRemaining = new[] { result.Primary, result.Secondary }
            .Where(w => w is not null)
            .Select(w => 100 - w!.UsedPercent)
            .DefaultIfEmpty(100)
            .Min();
        if (primaryUsed >= 100 || lowestRemaining <= 0)
        {
            StatusText = "已用尽";
            StatusBrush = UiBrushes.Orange;
            HintText = "主窗口已用尽 · 等待重置";
        }
        else if (result.IsStale)
        {
            StatusText = "缓存";
            StatusBrush = UiBrushes.Amber;
            HintText = result.Note ?? "本地快照，可能已过期";
        }
        else if (lowestRemaining <= 25)
        {
            StatusText = "注意";
            StatusBrush = UiBrushes.Amber;
            HintText = "额度偏低";
        }
        else
        {
            StatusText = "正常";
            StatusBrush = UiBrushes.Lime;
            HintText = "额度可用";
        }

        ResetCreditsText = result.ResetCredits switch
        {
            null => "重置次数未知",
            0 => "无可用重置",
            _ => $"{result.ResetCredits} 次重置可用"
        };
    }

    private void ApplyWindow(CodexQuotaWindow window, ref string label, ref string remainingText,
        ref double remaining, ref string resetText, ref Brush brush, string labelProperty,
        string remainingTextProperty, string remainingProperty, string resetProperty, string brushProperty)
    {
        var used = Math.Clamp(window.UsedPercent, 0, 100);
        var left = 100 - used;
        var tone = used >= 90 ? UiBrushes.Orange : used >= 70 ? UiBrushes.Amber : UiBrushes.Lime;

        label = CodexQuotaService.FormatWindowLabel(window.WindowMinutes);
        remainingText = $"{left}%";
        remaining = left;
        resetText = CodexQuotaService.FormatReset(window.ResetsAt, DateTimeOffset.Now);
        brush = tone;

        OnPropertyChanged(labelProperty);
        OnPropertyChanged(remainingTextProperty);
        OnPropertyChanged(remainingProperty);
        OnPropertyChanged(resetProperty);
        OnPropertyChanged(brushProperty);
    }

    public void Fail(string message)
    {
        IsLoading = false;
        IsUnavailable = true;
        HasData = false;
        StatusText = "不可用";
        StatusBrush = UiBrushes.Muted;
        HintText = message;
        PrimaryRemainingText = "—";
        SecondaryRemainingText = "—";
        PrimaryRemaining = 0;
        SecondaryRemaining = 0;
        PrimaryResetText = "请检查 Codex 登录";
        SecondaryResetText = string.Empty;
        PlanText = "—";
        SourceText = "未读取";
        ResetCreditsText = "—";
        NoteText = string.Empty;
    }
}

public sealed class DeepSeekBalanceCardViewModel : ObservableObject
{
    private string _balanceText = "—";
    private string _currencySymbol = "¥";
    private string _statusText = "未配置";
    private string _detailText = "设置 API Key 后读取余额";
    private string _hintText = "API Key 仅保存在本机";
    private string _updatedText = "尚未同步";
    private bool _hasKey;
    private bool _hasData;
    private bool _isUnavailable;
    private bool _isLoading;
    private Brush _statusBrush = UiBrushes.Muted;

    public string BalanceText { get => _balanceText; set => Set(ref _balanceText, value); }
    public string CurrencySymbol { get => _currencySymbol; set => Set(ref _currencySymbol, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }
    public string HintText { get => _hintText; set => Set(ref _hintText, value); }
    public string UpdatedText { get => _updatedText; set => Set(ref _updatedText, value); }
    public bool HasKey { get => _hasKey; set => Set(ref _hasKey, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public bool IsUnavailable { get => _isUnavailable; set => Set(ref _isUnavailable, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public void BeginLoading(bool hasKey)
    {
        HasKey = hasKey;
        IsLoading = true;
        IsUnavailable = false;
        if (HasData)
        {
            StatusText = "刷新中";
            StatusBrush = UiBrushes.Muted;
        }
        else if (!hasKey)
        {
            StatusText = "未配置";
            StatusBrush = UiBrushes.Muted;
        }
        else
        {
            StatusText = "读取中";
            StatusBrush = UiBrushes.Muted;
        }
    }

    public void Apply(DeepSeekBalanceResult result)
    {
        HasKey = true;
        HasData = true;
        IsUnavailable = false;
        IsLoading = false;
        BalanceText = DeepSeekService.FormatAmount(result.TotalBalance);
        CurrencySymbol = DeepSeekService.CurrencySymbol(result.Currency);
        DetailText = $"赠金 {CurrencySymbol}{DeepSeekService.FormatAmount(result.GrantedBalance)}  ·  充值 {CurrencySymbol}{DeepSeekService.FormatAmount(result.ToppedUpBalance)}";
        UpdatedText = $"{result.ObservedAt:HH:mm} 更新 · 余额接口";
        HintText = "余额仅用于读取，不会产生调用";
        if (result.IsAvailable)
        {
            StatusText = "可用";
            StatusBrush = UiBrushes.Lime;
        }
        else
        {
            StatusText = "余额不足";
            StatusBrush = UiBrushes.Orange;
        }
    }

    public void NotConfigured()
    {
        HasKey = false;
        HasData = false;
        IsUnavailable = false;
        IsLoading = false;
        BalanceText = "—";
        StatusText = "未配置";
        StatusBrush = UiBrushes.Muted;
        DetailText = "在设置中填入 API Key";
        HintText = "仅展示 · 不产生调用";
        UpdatedText = "尚未同步";
    }

    public void Fail(string message, bool hasKey = true)
    {
        HasKey = hasKey;
        IsLoading = false;
        IsUnavailable = true;
        if (!HasData)
        {
            BalanceText = "—";
            DetailText = "无法读取余额";
            UpdatedText = "同步失败";
        }
        else
        {
            UpdatedText = "显示上次结果 · 刷新失败";
        }

        StatusText = hasKey ? "连接失败" : "未配置";
        StatusBrush = hasKey ? UiBrushes.Orange : UiBrushes.Muted;
        HintText = message;
    }
}

public sealed class ClaudeBalanceCardViewModel : ObservableObject
{
    private string _balanceText = "—";
    private string _currencySymbol = string.Empty;
    private string _todayText = "今日 —";
    private string _statusText = "未配置";
    private string _hintText = "自动读取 Claude CLI 配置";
    private string _updatedText = "尚未同步";
    private bool _hasData;
    private bool _isConfigured;
    private bool _isLoading;
    private bool _isUnavailable;
    private Brush _statusBrush = UiBrushes.Muted;

    public string BalanceText { get => _balanceText; set => Set(ref _balanceText, value); }
    public string CurrencySymbol { get => _currencySymbol; set => Set(ref _currencySymbol, value); }
    public string TodayText { get => _todayText; set => Set(ref _todayText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string HintText { get => _hintText; set => Set(ref _hintText, value); }
    public string UpdatedText { get => _updatedText; set => Set(ref _updatedText, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public bool IsConfigured { get => _isConfigured; set => Set(ref _isConfigured, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool IsUnavailable { get => _isUnavailable; set => Set(ref _isUnavailable, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public void BeginLoading(bool configured)
    {
        IsConfigured = configured;
        IsLoading = true;
        IsUnavailable = false;
        if (HasData)
        {
            StatusText = "刷新中";
            StatusBrush = UiBrushes.Muted;
        }
        else
        {
            StatusText = configured ? "读取中" : "未配置";
            StatusBrush = UiBrushes.Muted;
        }
    }

    public void Apply(ClaudeUsageResult result)
    {
        HasData = true;
        IsConfigured = true;
        IsLoading = false;
        IsUnavailable = false;
        var symbol = ClaudeQuotaService.CurrencySymbol(result.Currency);
        CurrencySymbol = symbol;
        BalanceText = ClaudeQuotaService.FormatBalance(result.Balance);
        TodayText = $"今日 {symbol}{ClaudeQuotaService.FormatCost(result.TodayCost)}";
        UpdatedText = $"{result.ObservedAt:HH:mm} 更新 · 余额接口";
        HintText = "余额只读，不产生调用";
        if (!result.IsValid)
        {
            StatusText = "已失效";
            StatusBrush = UiBrushes.Orange;
        }
        else if (result.Balance <= 0)
        {
            StatusText = "余额不足";
            StatusBrush = UiBrushes.Orange;
        }
        else
        {
            StatusText = "可用";
            StatusBrush = UiBrushes.Lime;
        }
    }

    public void NotConfigured(string message = "未找到 Claude CLI 配置")
    {
        HasData = false;
        IsConfigured = false;
        IsLoading = false;
        IsUnavailable = false;
        BalanceText = "—";
        CurrencySymbol = string.Empty;
        TodayText = "今日 —";
        StatusText = "未配置";
        StatusBrush = UiBrushes.Muted;
        HintText = message;
        UpdatedText = "尚未同步";
    }

    public void Fail(string message, bool configured = true)
    {
        IsConfigured = configured;
        IsLoading = false;
        IsUnavailable = true;
        if (!HasData)
        {
            BalanceText = "—";
            CurrencySymbol = string.Empty;
            TodayText = "今日 —";
            UpdatedText = "同步失败";
        }
        else
        {
            UpdatedText = "显示上次结果 · 刷新失败";
        }

        StatusText = configured ? "连接失败" : "未配置";
        StatusBrush = configured ? UiBrushes.Orange : UiBrushes.Muted;
        HintText = message;
    }
}

public sealed class GeminiQuotaCardViewModel : ObservableObject
{
    private string _planText = string.Empty;
    private string _statusText = "未配置";
    private string _primaryLabel = "5 小时";
    private string _primaryRemainingText = "—";
    private double _primaryRemaining;
    private string _primaryResetText = "未配置";
    private string _secondaryLabel = "7 天";
    private string _secondaryRemainingText = "—";
    private double _secondaryRemaining;
    private string _secondaryResetText = "—";
    private string _sourceText = "尚未同步";
    private string _hintText = "通过本机 agy CLI 只读获取额度";
    private bool _hasData;
    private bool _isConfigured;
    private bool _isUnavailable;
    private bool _isLoading;
    private Brush _statusBrush = UiBrushes.Muted;
    private Brush _primaryBrush = UiBrushes.Muted;
    private Brush _secondaryBrush = UiBrushes.Muted;

    public string PlanText { get => _planText; set => Set(ref _planText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string PrimaryLabel { get => _primaryLabel; set => Set(ref _primaryLabel, value); }
    public string PrimaryRemainingText { get => _primaryRemainingText; set => Set(ref _primaryRemainingText, value); }
    public double PrimaryRemaining { get => _primaryRemaining; set => Set(ref _primaryRemaining, value); }
    public string PrimaryResetText { get => _primaryResetText; set => Set(ref _primaryResetText, value); }
    public string SecondaryLabel { get => _secondaryLabel; set => Set(ref _secondaryLabel, value); }
    public string SecondaryRemainingText { get => _secondaryRemainingText; set => Set(ref _secondaryRemainingText, value); }
    public double SecondaryRemaining { get => _secondaryRemaining; set => Set(ref _secondaryRemaining, value); }
    public string SecondaryResetText { get => _secondaryResetText; set => Set(ref _secondaryResetText, value); }
    public string SourceText { get => _sourceText; set => Set(ref _sourceText, value); }
    public string HintText { get => _hintText; set => Set(ref _hintText, value); }
    public bool HasData { get => _hasData; set => Set(ref _hasData, value); }
    public bool IsConfigured { get => _isConfigured; set => Set(ref _isConfigured, value); }
    public bool IsUnavailable { get => _isUnavailable; set => Set(ref _isUnavailable, value); }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
    public Brush PrimaryBrush { get => _primaryBrush; set => Set(ref _primaryBrush, value); }
    public Brush SecondaryBrush { get => _secondaryBrush; set => Set(ref _secondaryBrush, value); }

    public void BeginLoading(bool configured)
    {
        IsConfigured = configured;
        IsLoading = true;
        IsUnavailable = false;
        if (HasData)
        {
            StatusText = "刷新中";
            StatusBrush = UiBrushes.Muted;
        }
        else
        {
            StatusText = configured ? "读取中" : "未配置";
            StatusBrush = UiBrushes.Muted;
            HintText = configured ? "正在通过 agy CLI 读取额度" : "未检测到 agy CLI，可在设置中指定路径";
        }
    }

    public void Apply(GeminiQuotaResult result)
    {
        HasData = true;
        IsConfigured = true;
        IsUnavailable = false;
        IsLoading = false;
        PlanText = string.IsNullOrWhiteSpace(result.PlanType) ? string.Empty : result.PlanType.ToUpperInvariant();
        SourceText = $"{result.Source} · {result.ObservedAt:HH:mm}";

        if (result.Primary is not null)
        {
            ApplyWindow(result.Primary, ref _primaryLabel, ref _primaryRemainingText,
                ref _primaryRemaining, ref _primaryResetText, ref _primaryBrush,
                nameof(PrimaryLabel), nameof(PrimaryRemainingText), nameof(PrimaryRemaining),
                nameof(PrimaryResetText), nameof(PrimaryBrush));
        }
        else
        {
            PrimaryLabel = "5 小时";
            PrimaryRemainingText = "—";
            PrimaryRemaining = 0;
            PrimaryResetText = "暂无窗口";
            PrimaryBrush = UiBrushes.Muted;
        }

        if (result.Secondary is not null)
        {
            ApplyWindow(result.Secondary, ref _secondaryLabel, ref _secondaryRemainingText,
                ref _secondaryRemaining, ref _secondaryResetText, ref _secondaryBrush,
                nameof(SecondaryLabel), nameof(SecondaryRemainingText), nameof(SecondaryRemaining),
                nameof(SecondaryResetText), nameof(SecondaryBrush));
        }
        else
        {
            SecondaryLabel = "7 天";
            SecondaryRemainingText = "—";
            SecondaryRemaining = 0;
            SecondaryResetText = "暂无窗口";
            SecondaryBrush = UiBrushes.Muted;
        }

        var lowestRemaining = new[] { result.Primary, result.Secondary }
            .Where(w => w is not null)
            .Select(w => w!.RemainingPercent)
            .DefaultIfEmpty(100)
            .Min();

        if (lowestRemaining <= 0)
        {
            StatusText = "已用尽";
            StatusBrush = UiBrushes.Orange;
            HintText = "额度已用尽 · 等待重置";
        }
        else if (lowestRemaining <= 25)
        {
            StatusText = "注意";
            StatusBrush = UiBrushes.Amber;
            HintText = "Gemini 额度偏低";
        }
        else
        {
            StatusText = "正常";
            StatusBrush = UiBrushes.Lime;
            HintText = $"通过 {result.Source} 只读同步 · {result.ObservedAt:HH:mm}";
        }
    }

    private void ApplyWindow(GeminiQuotaWindow window, ref string label, ref string remainingText,
        ref double remaining, ref string resetText, ref Brush brush, string labelProperty,
        string remainingTextProperty, string remainingProperty, string resetProperty, string brushProperty)
    {
        var left = Math.Clamp(window.RemainingPercent, 0, 100);
        var used = 100 - left;
        var tone = used >= 90 ? UiBrushes.Orange : used >= 70 ? UiBrushes.Amber : UiBrushes.Lime;

        label = !string.IsNullOrWhiteSpace(window.Label)
            ? window.Label
            : CodexQuotaService.FormatWindowLabel(window.WindowMinutes);
        remainingText = $"{left}%";
        remaining = left;
        resetText = CodexQuotaService.FormatReset(window.ResetsAt, DateTimeOffset.Now);
        brush = tone;

        OnPropertyChanged(labelProperty);
        OnPropertyChanged(remainingTextProperty);
        OnPropertyChanged(remainingProperty);
        OnPropertyChanged(resetProperty);
        OnPropertyChanged(brushProperty);
    }

    public void NotConfigured(string message = "未检测到 agy CLI，可在设置中指定路径")
    {
        HasData = false;
        IsConfigured = false;
        IsLoading = false;
        IsUnavailable = false;
        PlanText = string.Empty;
        StatusText = "未配置";
        StatusBrush = UiBrushes.Muted;
        PrimaryLabel = "5 小时";
        SecondaryLabel = "7 天";
        PrimaryRemainingText = "—";
        SecondaryRemainingText = "—";
        PrimaryRemaining = 0;
        SecondaryRemaining = 0;
        PrimaryResetText = "待安装 agy";
        SecondaryResetText = "—";
        PrimaryBrush = UiBrushes.Muted;
        SecondaryBrush = UiBrushes.Muted;
        HintText = message;
        SourceText = "尚未同步";
    }

    public void Fail(string message, bool configured = true)
    {
        IsConfigured = configured;
        IsLoading = false;
        IsUnavailable = true;
        if (!HasData)
        {
            PlanText = string.Empty;
            PrimaryRemainingText = "—";
            SecondaryRemainingText = "—";
            PrimaryRemaining = 0;
            SecondaryRemaining = 0;
            PrimaryResetText = "读取失败";
            SecondaryResetText = "—";
            PrimaryBrush = UiBrushes.Muted;
            SecondaryBrush = UiBrushes.Muted;
            SourceText = "同步失败";
        }
        else
        {
            SourceText = "显示上次结果 · 刷新失败";
        }

        StatusText = configured ? "不可用" : "未配置";
        StatusBrush = configured ? UiBrushes.Orange : UiBrushes.Muted;
        HintText = message;
    }
}

public sealed class DashboardViewModel : ObservableObject
{
    private const int HistorySize = 42;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _memoryHistory = new();
    private string _cpuValue = "0.0";
    private string _memoryValue = "0.0";
    private double _cpuPercent;
    private double _memoryPercent;
    private string _memoryDetail = "— / —";
    private string _cpuTemp = "—";
    private string _integratedGpuName = "核显不可用";
    private string _integratedGpuUsage = "—";
    private string _integratedGpuTemp = "—";
    private string _discreteGpuName = "独显不可用";
    private string _discreteGpuUsage = "—";
    private string _discreteGpuTemp = "—";
    private string _netUp = "0 B/s";
    private string _netDown = "0 B/s";
    private string _adapterName = "无网络";
    private string _adapterDetail = "未检测到活动网卡";
    private string _sampledAt = "--:--:--";
    private string _cpuName = "本机处理器";
    private string _cpuDetail = "本机";
    private string _intervalText = "2 秒采样";
    private string _historyWindowText = "近 84 秒";
    private string _quotaRefreshText = "刷新间隔 5 分钟";
    private string _memoryBarDetail = "0.0 GB / 0.0 GB";
    private IReadOnlyList<double> _cpuSpark = new double[HistorySize];
    private IReadOnlyList<double> _memorySpark = new double[HistorySize];
    private Brush _tempBrush = UiBrushes.Lime;

    public CodexQuotaCardViewModel Codex { get; } = new();
    public GeminiQuotaCardViewModel Gemini { get; } = new();
    public ClaudeBalanceCardViewModel Claude { get; } = new();
    public DeepSeekBalanceCardViewModel DeepSeek { get; } = new();

    public string CpuValue { get => _cpuValue; set => Set(ref _cpuValue, value); }
    public string MemoryValue { get => _memoryValue; set => Set(ref _memoryValue, value); }
    public double CpuPercent { get => _cpuPercent; set => Set(ref _cpuPercent, value); }
    public double MemoryPercent { get => _memoryPercent; set => Set(ref _memoryPercent, value); }
    public string MemoryDetail { get => _memoryDetail; set => Set(ref _memoryDetail, value); }
    public string MemoryBarDetail { get => _memoryBarDetail; set => Set(ref _memoryBarDetail, value); }
    public string CpuTemp { get => _cpuTemp; set => Set(ref _cpuTemp, value); }
    public string IntegratedGpuName { get => _integratedGpuName; set => Set(ref _integratedGpuName, value); }
    public string IntegratedGpuUsage { get => _integratedGpuUsage; set => Set(ref _integratedGpuUsage, value); }
    public string IntegratedGpuTemp { get => _integratedGpuTemp; set => Set(ref _integratedGpuTemp, value); }
    public string DiscreteGpuName { get => _discreteGpuName; set => Set(ref _discreteGpuName, value); }
    public string DiscreteGpuUsage { get => _discreteGpuUsage; set => Set(ref _discreteGpuUsage, value); }
    public string DiscreteGpuTemp { get => _discreteGpuTemp; set => Set(ref _discreteGpuTemp, value); }
    public string NetUp { get => _netUp; set => Set(ref _netUp, value); }
    public string NetDown { get => _netDown; set => Set(ref _netDown, value); }
    public string AdapterName { get => _adapterName; set => Set(ref _adapterName, value); }
    public string AdapterDetail { get => _adapterDetail; set => Set(ref _adapterDetail, value); }
    public string SampledAt { get => _sampledAt; set => Set(ref _sampledAt, value); }
    public string CpuName { get => _cpuName; set => Set(ref _cpuName, value); }
    public string CpuDetail { get => _cpuDetail; set => Set(ref _cpuDetail, value); }
    public string IntervalText { get => _intervalText; set => Set(ref _intervalText, value); }
    public string HistoryWindowText { get => _historyWindowText; set => Set(ref _historyWindowText, value); }
    public string QuotaRefreshText { get => _quotaRefreshText; set => Set(ref _quotaRefreshText, value); }
    public IReadOnlyList<double> CpuSpark { get => _cpuSpark; set => Set(ref _cpuSpark, value); }
    public IReadOnlyList<double> MemorySpark { get => _memorySpark; set => Set(ref _memorySpark, value); }
    public Brush TempBrush { get => _tempBrush; set => Set(ref _tempBrush, value); }

    public void ApplyHardware(SystemSnapshot snapshot)
    {
        CpuValue = FormatPercent(snapshot.CpuPercent);
        MemoryValue = FormatPercent(snapshot.MemoryPercent);
        CpuPercent = snapshot.CpuPercent;
        MemoryPercent = snapshot.MemoryPercent;
        MemoryDetail = $"{snapshot.UsedMemoryGb:0.0} GB / {snapshot.TotalMemoryGb:0.0} GB";
        MemoryBarDetail = $"{snapshot.MemoryPercent:0}% 已使用";
        CpuName = snapshot.CpuName;
        CpuDetail = $"{snapshot.LogicalProcessors} 线程";
        CpuTemp = snapshot.CpuTemperatureC is null
            ? "—"
            : snapshot.CpuTemperatureC.Value.ToString("0.0", CultureInfo.InvariantCulture);
        TempBrush = snapshot.CpuTemperatureC is > 90 ? UiBrushes.Orange : UiBrushes.Lime;

        IntegratedGpuName = snapshot.IntegratedGpu.Available
            ? snapshot.IntegratedGpu.Name
            : "核显不可用";
        IntegratedGpuUsage = FormatMetric(snapshot.IntegratedGpu.UsagePercent);
        IntegratedGpuTemp = FormatMetric(snapshot.IntegratedGpu.TemperatureC);
        DiscreteGpuName = snapshot.DiscreteGpu.Available
            ? snapshot.DiscreteGpu.Name
            : "独显不可用";
        DiscreteGpuUsage = FormatMetric(snapshot.DiscreteGpu.UsagePercent);
        DiscreteGpuTemp = FormatMetric(snapshot.DiscreteGpu.TemperatureC);

        NetUp = SystemSampler.FormatRate(snapshot.UploadBytesPerSecond);
        NetDown = SystemSampler.FormatRate(snapshot.DownloadBytesPerSecond);
        AdapterName = snapshot.AdapterName;
        AdapterDetail = snapshot.AdapterDetail;
        SampledAt = snapshot.SampledAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        AddHistory(_cpuHistory, snapshot.CpuPercent);
        AddHistory(_memoryHistory, snapshot.MemoryPercent);
        CpuSpark = _cpuHistory.ToArray();
        MemorySpark = _memoryHistory.ToArray();
    }

    private static string FormatMetric(double? value) =>
        value is null ? "—" : value.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value)
    {
        value = Math.Clamp(value, 0, 100);
        // A leading zero keeps a seven-segment 7.4 from reading as 74.
        return value is > 0 and < 10
            ? value.ToString("00.0", CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void AddHistory(List<double> history, double value)
    {
        history.Add(Math.Clamp(value, 0, 100));
        while (history.Count > HistorySize)
        {
            history.RemoveAt(0);
        }
    }

    public void SetInterval(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        IntervalText = $"{seconds} 秒采样";
        HistoryWindowText = $"近 {HistorySize * seconds} 秒";
    }

    public void SetQuotaRefresh(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 60);
        QuotaRefreshText = $"刷新间隔 {minutes} 分钟";
    }

    public void ApplyDemo()
    {
        var now = DateTimeOffset.Now;
        _cpuHistory.Clear();
        _memoryHistory.Clear();
        for (var i = 0; i < HistorySize - 1; i++)
        {
            var cpu = 30.0 + 11.0 * Math.Sin(i * 0.34) + 4.0 * Math.Sin(i * 0.82);
            var memory = 61.0 + 3.0 * Math.Sin(i * 0.18);
            AddHistory(_cpuHistory, Math.Clamp(cpu, 4, 72));
            AddHistory(_memoryHistory, memory);
        }

        CpuSpark = _cpuHistory.ToArray();
        MemorySpark = _memoryHistory.ToArray();

        ApplyHardware(new SystemSnapshot(
            38.2, 62.4, 19.5, 31.2, 74.8,
            new GpuReading(true, "AMD Radeon 780M Graphics", 31.0, null),
            new GpuReading(true, "NVIDIA GeForce RTX 4060 Laptop GPU", 18.0, 65.7),
            1_258_291, 348_160, "以太网", string.Empty,
            now, "AMD Ryzen 9 7940HS", 16));
        Codex.Apply(new CodexQuotaResult(
            new CodexQuotaWindow(100, 300, DateTimeOffset.Now.AddMinutes(39).ToUnixTimeSeconds()),
            new CodexQuotaWindow(0, 10080, DateTimeOffset.Now.AddDays(6).ToUnixTimeSeconds()),
            "plus", 2, "APP-SERVER", now, false, null));
        Gemini.Apply(new GeminiQuotaResult(
            new GeminiQuotaWindow(86, 300, DateTimeOffset.Now.AddHours(2).AddMinutes(14).ToUnixTimeSeconds(), "5 小时"),
            new GeminiQuotaWindow(92, 10080, DateTimeOffset.Now.AddDays(5).ToUnixTimeSeconds(), "7 天"),
            "pro", "AGY-CLI", now, false, null));
        Claude.Apply(new ClaudeUsageResult(
            true, 76.18m, 0.89m, "USD", "unrestricted", now, "https://api.aijws.com/v1/usage"));
        DeepSeek.Apply(new DeepSeekBalanceResult(
            true, "CNY", 12.48m, 2.00m, 10.48m, now));
    }
}
