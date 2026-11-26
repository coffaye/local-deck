using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using LocalDeck.Services;
using LocalDeck.ViewModels;

namespace LocalDeck;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel _viewModel = new();
    private readonly SystemSampler _sampler = new();
    private readonly DeepSeekService _deepSeekService = new();
    private readonly ClaudeQuotaService _claudeService = new();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);
    private readonly SemaphoreSlim _codexGate = new(1, 1);
    private readonly SemaphoreSlim _claudeGate = new(1, 1);
    private readonly SemaphoreSlim _deepSeekGate = new(1, 1);

    private AppSettings _settings;
    private CodexQuotaService _codexService;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _hardwareCts;
    private CancellationTokenSource? _quotaCts;
    private bool _started;
    private bool _clearApiKeyPending;
    private bool _clearClaudeKeyPending;
    private bool _uiReady;
    private bool _exitRequested;
    private bool _titleBarCollapsed;
    private double _collapsedTitleBarHeight = 30;
    private System.Drawing.Icon? _trayIconImage;
    private HwndSource? _trayHwndSource;
    private HwndSourceHook? _trayHook;

    private bool _trayIconAdded;
    private bool _trayBalloonShown;
    private readonly string? _captureDirectory;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? captureDirectory)
    {
        InitializeComponent();
        _captureDirectory = captureDirectory;
        _settings = SettingsStore.Load();
        _codexService = new CodexQuotaService(_settings.CodexPath);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        Topmost = _settings.AlwaysOnTop;
        PinToggle.IsChecked = _settings.AlwaysOnTop;
        _viewModel.SetInterval(_settings.SampleSeconds);
        _viewModel.SetQuotaRefresh(_settings.QuotaRefreshMinutes);

        _uiReady = true;
        OpacitySlider.Value = Math.Clamp(_settings.WindowOpacity, 0.35, 1.0);
        Backdrop.Opacity = OpacitySlider.Value;
        HideTitleBarCheck.IsChecked = _settings.HideTitleBarOnDeactivation;
        ThemeCombo.SelectedIndex = Math.Clamp(_settings.UiTheme, 0, 1);
        ApplyTheme(ThemeCombo.SelectedIndex);
    }

    private void ApplyTheme(int theme)
    {
        var quotaPriority = theme == 1;
        if (quotaPriority)
        {
            Grid.SetRow(QuotaPanel, 0);
            Grid.SetRow(HeroRow, 1);
            Grid.SetRow(ChartPanel, 2);
            Grid.SetRow(SpecsRow, 3);

            MonitorPage.RowDefinitions[0].Height = new GridLength(140);
            MonitorPage.RowDefinitions[1].Height = new GridLength(70);
            MonitorPage.RowDefinitions[2].Height = new GridLength(58);
            MonitorPage.RowDefinitions[3].Height = new GridLength(48);

            HeroRow.RowDefinitions[0].Height = new GridLength(10);
            HeroRow.RowDefinitions[1].Height = new GridLength(40);
            HeroRow.RowDefinitions[2].Height = new GridLength(6);
            HeroRow.RowDefinitions[3].Height = new GridLength(14);
            CpuValueText.FontSize = 30;
            MemoryValueText.FontSize = 30;
            CpuPercentUnit.FontSize = 10;
            MemoryPercentUnit.FontSize = 10;
            CpuBar.Height = 4;
            MemoryBar.Height = 4;
            CodexPrimaryLabelText.Visibility = Visibility.Visible;
            CodexSecondaryLabelText.Visibility = Visibility.Visible;
            CodexPrimaryLabelText.FontSize = 9;
            CodexPrimaryLabelText.FontWeight = FontWeights.SemiBold;
            CodexSecondaryLabelText.FontSize = 9;
            CodexSecondaryLabelText.FontWeight = FontWeights.SemiBold;
            CodexPrimaryStack.Margin = new Thickness(0, 23, 0, 0);
            CodexSecondaryStack.Margin = new Thickness(0, 23, 0, 0);
            CodexPrimaryValueText.FontSize = 19;
            CodexSecondaryValueText.FontSize = 19;
            ClaudeBalanceValueText.FontSize = 24;
            DeepSeekBalanceValueText.FontSize = 24;
        }
        else
        {
            Grid.SetRow(HeroRow, 0);
            Grid.SetRow(ChartPanel, 1);
            Grid.SetRow(SpecsRow, 2);
            Grid.SetRow(QuotaPanel, 3);

            MonitorPage.RowDefinitions[0].Height = new GridLength(84);
            MonitorPage.RowDefinitions[1].Height = new GridLength(86);
            MonitorPage.RowDefinitions[2].Height = new GridLength(44);
            MonitorPage.RowDefinitions[3].Height = new GridLength(1, GridUnitType.Star);

            HeroRow.RowDefinitions[0].Height = new GridLength(11);
            HeroRow.RowDefinitions[1].Height = new GridLength(50);
            HeroRow.RowDefinitions[2].Height = new GridLength(7);
            HeroRow.RowDefinitions[3].Height = new GridLength(16);
            CpuValueText.FontSize = 40;
            MemoryValueText.FontSize = 40;
            CpuPercentUnit.FontSize = 12;
            MemoryPercentUnit.FontSize = 12;
            CpuBar.Height = 6;
            MemoryBar.Height = 6;
            CodexPrimaryLabelText.Visibility = Visibility.Visible;
            CodexSecondaryLabelText.Visibility = Visibility.Visible;
            CodexPrimaryLabelText.FontSize = 6;
            CodexPrimaryLabelText.FontWeight = FontWeights.Normal;
            CodexSecondaryLabelText.FontSize = 6;
            CodexSecondaryLabelText.FontWeight = FontWeights.Normal;
            CodexPrimaryStack.Margin = new Thickness(0);
            CodexSecondaryStack.Margin = new Thickness(0);
            CodexPrimaryValueText.FontSize = 18;
            CodexSecondaryValueText.FontSize = 18;
            ClaudeBalanceValueText.FontSize = 22;
            DeepSeekBalanceValueText.FontSize = 22;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (TopBarHost is null || !_titleBarCollapsed) return;

        // Expand the frame upward again; the content itself stays at the same
        // screen position because both the window top and height change.
        Height += _collapsedTitleBarHeight;
        Top -= _collapsedTitleBarHeight;
        TopBarHost.Visibility = Visibility.Visible;
        _titleBarCollapsed = false;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_settings.HideTitleBarOnDeactivation || TopBarHost is null || _titleBarCollapsed) return;
        if (WindowState != WindowState.Normal || !IsVisible) return;

        // Remove the entire title-bar row and move the window frame down by
        // the same amount, shrinking the window. The body keeps its 330px
        // height, so page content does not move at all.
        var barHeight = TopBarHost.ActualHeight > 0 ? TopBarHost.ActualHeight : 30;
        _collapsedTitleBarHeight = barHeight;
        _titleBarCollapsed = true;
        TopBarHost.Visibility = Visibility.Collapsed;
        Top += barHeight;
        Height -= barHeight;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || Backdrop is null) return;
        var value = Math.Clamp(e.NewValue, 0.35, 1.0);
        // Only the backdrop changes alpha; text and controls stay fully opaque.
        Backdrop.Opacity = value;
        _settings.WindowOpacity = value;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Rounded corners are cosmetic and Windows-version dependent.
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        _lifetimeCts = new CancellationTokenSource();

        LoadSettingsForm();
        if (_captureDirectory is null)
        {
            InitializeTrayIcon();
        }

        StartupRegistration.Apply(_settings.StartWithWindows);

        if (App.DemoMode)
        {
            _viewModel.ApplyDemo();
            _viewModel.SetInterval(_settings.SampleSeconds);
            if (!string.IsNullOrWhiteSpace(_captureDirectory))
            {
                _ = CapturePagesAsync(_captureDirectory);
            }

            return;
        }

        _viewModel.Codex.BeginLoading();
        var claudeConfig = ClaudeConfigReader.Resolve(_settings);
        if (claudeConfig.IsConfigured)
        {
            _viewModel.Claude.BeginLoading(true);
        }
        else
        {
            _viewModel.Claude.NotConfigured("未找到 Claude CLI 配置，可在设置中填写");
        }
        if (_settings.DeepSeekConfigured)
        {
            _viewModel.DeepSeek.BeginLoading(true);
        }
        else
        {
            _viewModel.DeepSeek.NotConfigured();
        }

        RestartHardwareLoop();
        RestartQuotaLoop();

        if (!string.IsNullOrWhiteSpace(_captureDirectory))
        {
            _ = CapturePagesAsync(_captureDirectory, 15000);
        }

        await Task.CompletedTask;
    }

    private async Task CapturePagesAsync(string directory, int initialDelayMs = 700)
    {
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(initialDelayMs);

            await CaptureCurrentAsync(Path.Combine(directory, "01-monitor.png"));
            ShowSettings_Click(this, new RoutedEventArgs());
            await Task.Delay(350);
            await CaptureCurrentAsync(Path.Combine(directory, "02-settings.png"));

            ShowMonitor_Click(this, new RoutedEventArgs());
            await Task.Delay(300);
            await CaptureCurrentAsync(Path.Combine(directory, "03-monitor-2x.png"), 2.0);
            await CaptureCurrentAsync(Path.Combine(directory, "04-monitor-1x.png"), 1.0);

            ShowSettings_Click(this, new RoutedEventArgs());
            await Task.Delay(300);
            await CaptureCurrentAsync(Path.Combine(directory, "05-settings-1x.png"), 1.0);

            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "capture-error.txt"), ex.ToString());
            }
            catch
            {
                // Nothing else to do.
            }

            Application.Current.Shutdown(2);
        }
    }

    private async Task CaptureCurrentAsync(string path, double scale = 0)
    {
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = scale > 0 ? scale : dpi.DpiScaleX;
        var scaleY = scale > 0 ? scale : dpi.DpiScaleY;
        var pixelWidth = Math.Max(1, (int)Math.Round(Frame.ActualWidth * scaleX));
        var pixelHeight = Math.Max(1, (int)Math.Round(Frame.ActualHeight * scaleY));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scaleX, 96 * scaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(Frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    private void RestartHardwareLoop()
    {
        if (App.DemoMode) return;
        _hardwareCts?.Cancel();
        if (_lifetimeCts is null) return;
        _hardwareCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _ = HardwareLoopAsync(_hardwareCts.Token);
    }

    private async Task HardwareLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _sampleGate.WaitAsync(cancellationToken);
                SystemSnapshot snapshot;
                try
                {
                    var adapterId = _settings.NetworkAdapterId;
                    snapshot = await Task.Run(() => _sampler.Sample(adapterId), cancellationToken);
                }
                finally
                {
                    _sampleGate.Release();
                }

                await Dispatcher.InvokeAsync(() => _viewModel.ApplyHardware(snapshot),
                    DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Hardware sensors can be transiently unavailable; keep the loop alive.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_settings.SampleSeconds, 1, 60)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RestartQuotaLoop()
    {
        if (App.DemoMode) return;
        _quotaCts?.Cancel();
        if (_lifetimeCts is null) return;
        _quotaCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _ = QuotaLoopAsync(_quotaCts.Token);
    }

    private async Task QuotaLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshQuotasAsync(cancellationToken);
            using var timer = new PeriodicTimer(
                TimeSpan.FromMinutes(Math.Clamp(_settings.QuotaRefreshMinutes, 1, 60)));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshQuotasAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or settings changed.
        }
        catch
        {
            // A dashboard refresh must never take down the window.
        }
    }

    private async Task RefreshQuotasAsync(CancellationToken cancellationToken)
    {
        if (App.DemoMode) return;
        var codex = RefreshCodexAsync(cancellationToken);
        var claude = RefreshClaudeAsync(cancellationToken);
        var deepSeek = RefreshDeepSeekAsync(cancellationToken);
        await Task.WhenAll(codex, claude, deepSeek);
    }

    private async Task RefreshCodexAsync(CancellationToken cancellationToken)
    {
        if (!await _codexGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await Dispatcher.InvokeAsync(_viewModel.Codex.BeginLoading);
            var result = await Task.Run(() => _codexService.GetQuotaAsync(cancellationToken),
                cancellationToken);
            await Dispatcher.InvokeAsync(() => _viewModel.Codex.Apply(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing.
        }
        catch (CodexQuotaException ex)
        {
            await Dispatcher.InvokeAsync(() => _viewModel.Codex.Fail(ex.Message));
        }
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() =>
                _viewModel.Codex.Fail("未能读取 Codex 额度。请确认 Codex 已登录。"));
        }
        finally
        {
            _codexGate.Release();
        }
    }

    private async Task RefreshClaudeAsync(CancellationToken cancellationToken)
    {
        ClaudeResolvedConfig config;
        try
        {
            config = ClaudeConfigReader.Resolve(_settings);
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
                _viewModel.Claude.NotConfigured("无法读取 Claude 配置"));
            return;
        }

        if (!config.IsConfigured)
        {
            await Dispatcher.InvokeAsync(() =>
                _viewModel.Claude.NotConfigured(config.HasToken
                    ? "未找到 Claude 查询地址，可在设置中填写"
                    : "未找到 Claude CLI 配置，可在设置中填写"));
            return;
        }

        if (!await _claudeGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await Dispatcher.InvokeAsync(() => _viewModel.Claude.BeginLoading(true));
            var result = await Task.Run(
                () => _claudeService.GetUsageAsync(config, cancellationToken),
                cancellationToken);
            await Dispatcher.InvokeAsync(() => _viewModel.Claude.Apply(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing.
        }
        catch (ClaudeQuotaException ex)
        {
            await Dispatcher.InvokeAsync(() => _viewModel.Claude.Fail(ex.Message));
        }
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() => _viewModel.Claude.Fail("Claude 额度刷新失败"));
        }
        finally
        {
            _claudeGate.Release();
        }
    }

    private async Task RefreshDeepSeekAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
        {
            await Dispatcher.InvokeAsync(_viewModel.DeepSeek.NotConfigured);
            return;
        }

        if (!await _deepSeekGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var hasKey = !string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey);
            await Dispatcher.InvokeAsync(() => _viewModel.DeepSeek.BeginLoading(hasKey));
            var key = _settings.DeepSeekApiKey;
            var result = await Task.Run(() => _deepSeekService.GetBalanceAsync(key, cancellationToken),
                cancellationToken);
            await Dispatcher.InvokeAsync(() => _viewModel.DeepSeek.Apply(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing.
        }
        catch (DeepSeekBalanceException ex)
        {
            await Dispatcher.InvokeAsync(() =>
                _viewModel.DeepSeek.Fail(ex.Message, _settings.DeepSeekConfigured));
        }
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() =>
                _viewModel.DeepSeek.Fail("余额刷新失败", _settings.DeepSeekConfigured));
        }
        finally
        {
            _deepSeekGate.Release();
        }
    }

    private void LoadSettingsForm()
    {
        CodexPathBox.Text = string.IsNullOrWhiteSpace(_settings.CodexPath)
            ? _codexService.FindCodexExecutable() ?? string.Empty
            : _settings.CodexPath;
        UpdateCodexDetectionText();

        ApiKeyBox.Clear();
        _clearApiKeyPending = false;
        ApiKeyStatusText.Text = _settings.DeepSeekConfigured
            ? _settings.ApiKeyCameFromEnvironment
                ? "环境变量密钥 · 仅本次运行"
                : "密钥已保存 · 留空不修改"
            : "尚未配置";
        ApiKeyStatusText.Foreground = _settings.DeepSeekConfigured ? UiBrushes.Lime : UiBrushes.Muted;

        ClaudeApiKeyBox.Clear();
        _clearClaudeKeyPending = false;
        var claudeConfig = ClaudeConfigReader.Resolve(_settings);
        ClaudeQueryUrlBox.Text = string.IsNullOrWhiteSpace(_settings.ClaudeQueryUrl)
            ? claudeConfig.QueryUrl ?? string.Empty
            : _settings.ClaudeQueryUrl;
        UpdateClaudeSettingsStatus(claudeConfig);

        LoadNetworkChoices();
        SelectQuotaRefresh(_settings.QuotaRefreshMinutes);
        SelectHardwareInterval(_settings.SampleSeconds);
        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        HideTitleBarCheck.IsChecked = _settings.HideTitleBarOnDeactivation;
        ThemeCombo.SelectedIndex = Math.Clamp(_settings.UiTheme, 0, 1);
        SettingsStatusText.Text = string.Empty;
    }

    private void LoadNetworkChoices()
    {
        var selectedId = _settings.NetworkAdapterId;
        var choices = new List<AdapterChoice>
        {
            new(string.Empty, "自动选择")
        };

        try
        {
            foreach (var adapter in _sampler.GetNetworkAdapters())
            {
                var suffix = adapter.IsConnected ? string.Empty : " · 未连接";
                choices.Add(new AdapterChoice(adapter.Id, $"{adapter.Name} · {adapter.TypeLabel}{suffix}"));
            }
        }
        catch
        {
            // Keep the automatic option.
        }

        NetworkCombo.ItemsSource = choices;
        var selected = choices.FirstOrDefault(c => c.Id == selectedId) ?? choices[0];
        NetworkCombo.SelectedItem = selected;
    }

    private void SelectQuotaRefresh(int minutes)
    {
        foreach (var item in QuotaRefreshCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == minutes)
            {
                QuotaRefreshCombo.SelectedItem = item;
                return;
            }
        }

        QuotaRefreshCombo.SelectedIndex = 1;
    }

    private void SelectHardwareInterval(int seconds)
    {
        foreach (var item in HardwareIntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == seconds)
            {
                HardwareIntervalCombo.SelectedItem = item;
                return;
            }
        }

        HardwareIntervalCombo.SelectedIndex = 1;
    }

    private void UpdateCodexDetectionText()
    {
        var detected = _codexService.FindCodexExecutable();
        if (!string.IsNullOrWhiteSpace(detected))
        {
            CodexDetectText.Text = "自动检测可用 · 只读启动 app-server";
            CodexDetectText.ToolTip = detected;
        }
        else
        {
            CodexDetectText.Text = "未检测到 Codex CLI · 将尝试本机登录信息";
            CodexDetectText.ToolTip = null;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.CpuValue))
        {
            Pulse(CpuValueText);
        }
        else if (e.PropertyName == nameof(DashboardViewModel.MemoryValue))
        {
            Pulse(MemoryValueText);
        }
    }

    private static void Pulse(UIElement element)
    {
        var animation = new DoubleAnimation
        {
            From = 0.55,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private void ShowPage(FrameworkElement page, Button activeTab)
    {
        var pages = new FrameworkElement[] { MonitorPage, SettingsPage };
        foreach (var item in pages)
        {
            if (ReferenceEquals(item, page)) continue;
            item.Visibility = Visibility.Collapsed;
        }

        if (page.Visibility != Visibility.Visible || page.Opacity < 1)
        {
            page.Visibility = Visibility.Visible;
            page.Opacity = 0;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        foreach (var tab in new[] { TabMonitorButton, TabSettingsButton })
        {
            tab.Style = (Style)FindResource(ReferenceEquals(tab, activeTab) ? "NavTabActiveStyle" : "NavTabStyle");
        }

        if (ReferenceEquals(page, SettingsPage))
        {
            LoadSettingsForm();
        }
    }

    private void ShowMonitor_Click(object sender, RoutedEventArgs e) =>
        ShowPage(MonitorPage, TabMonitorButton);

    private void ShowSettings_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage, TabSettingsButton);

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse was released early.
        }
    }

    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = PinToggle.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch
        {
            // Topmost still applies for this session.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async void RefreshQuota_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetimeCts is null) return;
        try
        {
            await RefreshQuotasAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
    }

    private async void RefreshBalance_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetimeCts is null) return;
        try
        {
            await RefreshDeepSeekAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
    }

    private void OpenSettingsFromDeepSeek_Click(object sender, RoutedEventArgs e) =>
        ShowSettings_Click(sender, e);

    private void UpdateClaudeSettingsStatus(ClaudeResolvedConfig? config = null)
    {
        config ??= ClaudeConfigReader.Resolve(_settings);
        var hasOverride = !string.IsNullOrWhiteSpace(_settings.ClaudeApiKeyProtected) ||
                          !string.IsNullOrWhiteSpace(_settings.ClaudeApiKey);
        var keySource = hasOverride
            ? "自定义 Key"
            : config.HasToken
                ? $"Key: {config.TokenSource}"
                : "未找到 Key";
        ClaudeStatusText.Text = config.IsConfigured
            ? $"已就绪 · {keySource}"
            : $"未配置 · {keySource}";
        ClaudeStatusText.Foreground = config.IsConfigured ? UiBrushes.Lime : UiBrushes.Muted;
        ClaudeStatusText.ToolTip = config.QueryUrl;
    }

    private void AutoDetectClaude_Click(object sender, RoutedEventArgs e)
    {
        var config = ClaudeConfigReader.Resolve(_settings, ignoreSettings: true);
        ClaudeQueryUrlBox.Text = config.QueryUrl ?? string.Empty;
        ClaudeStatusText.Text = config.HasToken
            ? config.HasQueryUrl
                ? "已读取 Claude CLI 配置 · 可保存"
                : "已读取 Key，但未找到查询地址"
            : "未找到 Claude CLI 配置，请手动填写";
        ClaudeStatusText.Foreground = config.IsConfigured ? UiBrushes.Lime : UiBrushes.Amber;
        ClaudeStatusText.ToolTip = config.QueryUrl;
    }

    private async void TestClaude_Click(object sender, RoutedEventArgs e)
    {
        var resolved = ClaudeConfigReader.Resolve(_settings);
        var key = ClaudeApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key)) key = resolved.Token ?? string.Empty;
        var url = ClaudeQueryUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) url = resolved.QueryUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            ClaudeStatusText.Foreground = UiBrushes.Orange;
            ClaudeStatusText.Text = "请填写 Claude Key，或先配置 Claude CLI";
            return;
        }
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ClaudeStatusText.Foreground = UiBrushes.Orange;
            ClaudeStatusText.Text = "请填写有效的 Claude 查询地址";
            return;
        }

        ClaudeStatusText.Foreground = UiBrushes.Muted;
        ClaudeStatusText.Text = "正在连接 Claude…";
        try
        {
            var config = new ClaudeResolvedConfig(true, true, key, url, null, false, "测试", "测试");
            var result = await _claudeService.GetUsageAsync(config, CancellationToken.None);
            _viewModel.Claude.Apply(result);
            var symbol = ClaudeQuotaService.CurrencySymbol(result.Currency);
            ClaudeStatusText.Foreground = result.IsValid ? UiBrushes.Lime : UiBrushes.Orange;
            ClaudeStatusText.Text =
                $"连接成功 · 余额 {symbol}{ClaudeQuotaService.FormatBalance(result.Balance)}（尚未保存）";
        }
        catch (ClaudeQuotaException ex)
        {
            ClaudeStatusText.Foreground = UiBrushes.Orange;
            ClaudeStatusText.Text = ex.Message;
        }
        catch (Exception)
        {
            ClaudeStatusText.Foreground = UiBrushes.Orange;
            ClaudeStatusText.Text = "Claude 连接失败";
        }
    }

    private void ClearClaudeKey_Click(object sender, RoutedEventArgs e)
    {
        _clearClaudeKeyPending = true;
        ClaudeApiKeyBox.Clear();
        ClaudeStatusText.Foreground = UiBrushes.Amber;
        ClaudeStatusText.Text = "已清除自定义 Key · 保存后改用 Claude CLI 配置";
    }

    private void BrowseCodex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 Codex CLI",
            Filter = "Codex CLI (codex.exe)|codex.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            CodexPathBox.Text = dialog.FileName;
            CodexDetectText.Text = "已选择自定义 Codex CLI";
            CodexDetectText.ToolTip = dialog.FileName;
        }
    }

    private void AutoDetectCodex_Click(object sender, RoutedEventArgs e)
    {
        var detected = CodexQuotaService.FindCodexExecutable(string.Empty);
        if (string.IsNullOrWhiteSpace(detected))
        {
            CodexPathBox.Text = string.Empty;
            CodexDetectText.Text = "未检测到 Codex CLI";
            CodexDetectText.ToolTip = null;
            return;
        }

        CodexPathBox.Text = detected;
        CodexDetectText.Text = "自动检测可用 · 只读启动 app-server";
        CodexDetectText.ToolTip = detected;
    }

    private async void TestDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        var key = !string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? ApiKeyBox.Password.Trim()
            : _clearApiKeyPending ? string.Empty : _settings.DeepSeekApiKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            SettingsStatusText.Foreground = UiBrushes.Orange;
            SettingsStatusText.Text = "请先填入 DeepSeek 密钥";
            return;
        }

        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        SettingsStatusText.Foreground = UiBrushes.Muted;
        SettingsStatusText.Text = "正在连接 DeepSeek…";
        try
        {
            var result = await Task.Run(() => _deepSeekService.GetBalanceAsync(key));
            _viewModel.DeepSeek.Apply(result);
            SettingsStatusText.Foreground = UiBrushes.Lime;
            SettingsStatusText.Text =
                $"连接成功 · 余额 {DeepSeekService.CurrencySymbol(result.Currency)}{DeepSeekService.FormatAmount(result.TotalBalance)}（尚未保存）";
        }
        catch (DeepSeekBalanceException ex)
        {
            SettingsStatusText.Foreground = UiBrushes.Orange;
            SettingsStatusText.Text = ex.Message;
        }
        catch (Exception)
        {
            SettingsStatusText.Foreground = UiBrushes.Orange;
            SettingsStatusText.Text = "测试失败，请检查网络";
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Clear();
        _clearApiKeyPending = true;
        ApiKeyStatusText.Text = "已移除 · 保存后生效";
        ApiKeyStatusText.Foreground = UiBrushes.Amber;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var replacementKey = !string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? ApiKeyBox.Password.Trim()
            : _clearApiKeyPending ? string.Empty : null;
        var replacementClaudeKey = !string.IsNullOrWhiteSpace(ClaudeApiKeyBox.Password)
            ? ClaudeApiKeyBox.Password.Trim()
            : _clearClaudeKeyPending ? string.Empty : null;

        _settings.CodexPath = CodexPathBox.Text.Trim();
        _settings.ClaudeQueryUrl = ClaudeQueryUrlBox.Text.Trim();
        _settings.NetworkAdapterId =
            NetworkCombo.SelectedItem is AdapterChoice choice ? choice.Id : string.Empty;
        _settings.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _settings.HideTitleBarOnDeactivation = HideTitleBarCheck.IsChecked == true;
        _settings.UiTheme = Math.Clamp(ThemeCombo.SelectedIndex, 0, 1);
        ApplyTheme(_settings.UiTheme);
        _settings.WindowOpacity = Backdrop.Opacity;
        if (QuotaRefreshCombo.SelectedItem is ComboBoxItem quotaItem &&
            quotaItem.Tag is string tag && int.TryParse(tag, out var minutes))
        {
            _settings.QuotaRefreshMinutes = Math.Clamp(minutes, 1, 60);
        }

        if (HardwareIntervalCombo.SelectedItem is ComboBoxItem hardwareItem &&
            hardwareItem.Tag is string hardwareTag && int.TryParse(hardwareTag, out var seconds))
        {
            _settings.SampleSeconds = Math.Clamp(seconds, 1, 60);
        }

        try
        {
            SettingsStore.Save(_settings, replacementKey, replacementClaudeKey);
            _settings.LoadFailed = false;
        }
        catch (Exception)
        {
            SettingsStatusText.Foreground = UiBrushes.Orange;
            SettingsStatusText.Text = "保存失败，请检查设置文件权限";
            return;
        }

        _clearApiKeyPending = false;
        ApiKeyBox.Clear();
        ApiKeyStatusText.Foreground = _settings.DeepSeekConfigured ? UiBrushes.Lime : UiBrushes.Muted;
        ApiKeyStatusText.Text = _settings.DeepSeekConfigured
            ? _settings.ApiKeyCameFromEnvironment
                ? "环境变量密钥 · 仅本次运行"
                : "密钥已保存 · 留空不修改"
            : "尚未配置";

        _clearClaudeKeyPending = false;
        ClaudeApiKeyBox.Clear();
        var claudeConfig = ClaudeConfigReader.Resolve(_settings);
        if (string.IsNullOrWhiteSpace(_settings.ClaudeQueryUrl))
        {
            ClaudeQueryUrlBox.Text = claudeConfig.QueryUrl ?? string.Empty;
        }
        UpdateClaudeSettingsStatus(claudeConfig);
        if (replacementClaudeKey is not null)
        {
            _viewModel.Claude.NotConfigured();
        }

        Topmost = _settings.AlwaysOnTop;
        PinToggle.IsChecked = _settings.AlwaysOnTop;
        StartupRegistration.Apply(_settings.StartWithWindows);

        _codexService = new CodexQuotaService(_settings.CodexPath);
        UpdateCodexDetectionText();
        RestartQuotaLoop();
        _viewModel.SetQuotaRefresh(_settings.QuotaRefreshMinutes);
        _viewModel.SetInterval(_settings.SampleSeconds);
        RestartHardwareLoop();

        SettingsStatusText.Foreground = UiBrushes.Lime;
        SettingsStatusText.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";

        if (replacementKey is not null)
        {
            // A different key (or a cleared key) must not keep showing an old account's balance.
            _viewModel.DeepSeek.NotConfigured();
        }

        if (string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
        {
            _viewModel.DeepSeek.NotConfigured();
        }

        if (_lifetimeCts is not null)
        {
            try
            {
                await RefreshQuotasAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Window closing; the settings were already saved.
            }
            catch
            {
                SettingsStatusText.Foreground = UiBrushes.Amber;
                SettingsStatusText.Text = "设置已保存，但额度刷新失败";
            }
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            LoadTrayIcon();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            _trayHwndSource = HwndSource.FromHwnd(hwnd);
            if (_trayHwndSource is null) return;
            _trayHook = TrayWndProc;
            _trayHwndSource.AddHook(_trayHook);

            if (TryAddTrayIcon())
            {
                _ = PromoteTrayIconAsync();
            }
        }
        catch
        {
            _trayIconAdded = false;
        }
    }

    private async Task PromoteTrayIconAsync()
    {
        // Windows 11 puts new notification icons in the overflow by default.
        // Try to mark this app's icon as promoted for future shell sessions.
        // This is best-effort and never affects the running window.
        try
        {
            await Task.Delay(2000);
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var fullExe = Path.GetFullPath(exe);

            using var root = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;

            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name, writable: true);
                if (entry?.GetValue("ExecutablePath") is not string rawPath ||
                    string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(
                        Environment.ExpandEnvironmentVariables(rawPath).Trim().Trim('"'));
                }
                catch
                {
                    continue;
                }

                if (!string.Equals(candidate, fullExe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Do not override an explicit unpin chosen by the user.
                if (entry.GetValue("IsPromoted") is null)
                {
                    entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch
        {
            // Promotion is a convenience; the user can still drag the icon out.
        }
    }

    private bool TryAddTrayIcon()
    {
        if (_trayIconAdded) return true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            var data = CreateTrayData(hwnd);
            _trayIconAdded = Shell_NotifyIcon(NimAdd, ref data);
            return _trayIconAdded;
        }
        catch
        {
            return false;
        }
    }

    private void LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/assets/LocalDeck.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var baseIcon = new System.Drawing.Icon(stream);
                _trayIconImage = new System.Drawing.Icon(baseIcon, 16, 16);
            }
        }
        catch
        {
            _trayIconImage = null;
        }

        if (_trayIconImage is not null) return;

        try
        {
            var path = Environment.ProcessPath;
            if (path is null) return;
            using var baseIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (baseIcon is not null)
            {
                _trayIconImage = new System.Drawing.Icon(baseIcon, 16, 16);
            }
        }
        catch
        {
            _trayIconImage = null;
        }
    }

    private NOTIFYICONDATA CreateTrayData(IntPtr hwnd)
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = (uint)(Environment.ProcessId & 0x7FFFFFFF),
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _trayIconImage?.Handle ?? IntPtr.Zero,
            szTip = "桌仪",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private IntPtr TrayWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != TrayCallbackMessage) return IntPtr.Zero;

        var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMessage)
        {
            case WmLeftButtonUp:
            case WmLeftButtonDoubleClick:
                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                handled = true;
                break;
            case WmRightButtonUp:
            case WmContextMenu:
                Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendMenuW(menu, MfString, 1, "打开");
                AppendMenuW(menu, MfString, 2, "退出");
                GetCursorPos(out var point);
                SetForegroundWindow(hwnd);
                var command = TrackPopupMenuEx(menu,
                    TpmReturnCmd | TpmRightButton | TpmLeftAlign | TpmTopAlign,
                    point.X, point.Y, hwnd, IntPtr.Zero);
                if (command == 1)
                {
                    RestoreFromTray();
                }
                else if (command == 2)
                {
                    ExitFromTray();
                }
            }
            finally
            {
                DestroyMenu(menu);
            }
        }
        catch
        {
            // The native menu is best-effort; never crash the tray callback.
        }
    }

    private void ShowTrayBalloon()
    {
        if (!_trayIconAdded) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var data = CreateTrayData(hwnd);
            data.uFlags = NifInfo;
            data.szInfoTitle = "桌仪";
            data.szInfo = "已隐藏到右下角托盘。若没看到 77 图标，点任务栏的 ^ 展开隐藏图标。";
            data.dwInfoFlags = NiifInfo;
            data.uTimeoutOrVersion = 3000;
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch
        {
            // A balloon is only a hint; never block hiding.
        }
    }

    private void HideToTray()
    {
        PersistSettingsQuietly();
        TryAddTrayIcon();
        if (!_trayIconAdded)
        {
            // If the shell refuses the tray icon, keep a taskbar button rather
            // than making the app disappear.
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
        if (!_trayBalloonShown)
        {
            _trayBalloonShown = true;
            ShowTrayBalloon();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = _settings.AlwaysOnTop;
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        DisposeTrayIcon();
        Close();
    }

    private void DisposeTrayIcon()
    {
        try
        {
            if (_trayIconAdded)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var data = CreateTrayData(hwnd);
                    Shell_NotifyIcon(NimDelete, ref data);
                }

                _trayIconAdded = false;
            }

            if (_trayHwndSource is not null && _trayHook is not null)
            {
                _trayHwndSource.RemoveHook(_trayHook);
            }

            _trayHwndSource = null;
            _trayHook = null;

            _trayIconImage?.Dispose();
            _trayIconImage = null;
        }
        catch
        {
            // Tray cleanup is best-effort.
        }
    }

    private void PersistSettingsQuietly()
    {
        if (_settings.LoadFailed) return;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch
        {
            // Persistence must never block hiding/closing.
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _captureDirectory is null && !Environment.HasShutdownStarted && _trayIconAdded)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _lifetimeCts?.Cancel();
        _hardwareCts?.Cancel();
        _quotaCts?.Cancel();
        PersistSettingsQuietly();
        DisposeTrayIcon();
        _sampler.Dispose();
    }

    private const int TrayCallbackMessage = 0x8000 + 77;
    private const int WmContextMenu = 0x007B;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI
    {
        public int X;
        public int Y;
    }

    private const uint MfString = 0x00000000;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, uint itemId, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointI point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr hwnd, IntPtr parameters);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed record AdapterChoice(string Id, string Display)
    {
        public override string ToString() => Display;
    }
}







