using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LocalDeck.Services;

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string TypeLabel,
    bool IsConnected);

public sealed record GpuReading(
    bool Available,
    string Name,
    double? UsagePercent,
    double? TemperatureC);

public sealed record SystemSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double UsedMemoryGb,
    double TotalMemoryGb,
    double? CpuTemperatureC,
    GpuReading IntegratedGpu,
    GpuReading DiscreteGpu,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    string AdapterName,
    string AdapterDetail,
    DateTimeOffset SampledAt,
    string CpuName,
    int LogicalProcessors);

public sealed class SystemSampler : IDisposable
{
    private ThermalZoneReader? _thermal;
    private NvidiaGpuReader? _gpu;
    private LibreIntegratedGpuReader? _integratedGpu;
    private readonly string _cpuName;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _cpuInitialized;
    private string? _activeAdapterId;
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private long _lastNetworkTimestamp;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private bool _disposed;

    public SystemSampler()
    {
        _cpuName = ReadCpuName();
        GetSystemTimes(out var idle, out var kernel, out var user);
        _lastIdle = idle.ToUInt64();
        _lastKernel = kernel.ToUInt64();
        _lastUser = user.ToUInt64();
        _cpuInitialized = _lastKernel != 0 || _lastUser != 0;

        // Hardware enumeration is expensive (especially LibreHardwareMonitor).
        // Start it in the background so the window can paint immediately.
        _ = Task.Run(InitializeDevices);
    }

    private void InitializeDevices()
    {
        var thermal = TryCreate(() => new ThermalZoneReader());
        if (thermal is not null)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    thermal.Dispose();
                    return;
                }

                _thermal = thermal;
            }
        }

        var gpu = TryCreate(() => new NvidiaGpuReader());
        if (gpu is not null)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    gpu.Dispose();
                    return;
                }

                _gpu = gpu;
            }
        }

        var integrated = TryCreate(() => new LibreIntegratedGpuReader());
        if (integrated is not null)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    integrated.Dispose();
                    return;
                }

                _integratedGpu = integrated;
            }
        }
    }

    private static T? TryCreate<T>(Func<T> factory) where T : class, IDisposable
    {
        try
        {
            return factory();
        }
        catch
        {
            return null;
        }
    }

    public string CpuName => _cpuName;
    public int LogicalProcessors => Environment.ProcessorCount;
    public IReadOnlyList<NetworkAdapterInfo> GetNetworkAdapters() => NetworkSampler.GetAdapters();

    public SystemSnapshot Sample(string? preferredAdapterId)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SystemSampler));
            return SampleCore(preferredAdapterId);
        }
    }

    private SystemSnapshot SampleCore(string? preferredAdapterId)
    {
        SampleCpu(out var cpuPercent);
        SampleMemory(out var memPercent, out var usedGb, out var totalGb);
        var thermal = _thermal;
        var integrated = _integratedGpu;
        var discrete = _gpu;
        var temp = thermal?.ReadCelsius();
        var integratedGpu = integrated is null
            ? new GpuReading(false, "核显加载中", null, null)
            : new GpuReading(
                integrated.IsAvailable,
                integrated.Name,
                integrated.ReadUsagePercent(),
                null);
        var discreteGpu = discrete is null
            ? new GpuReading(false, "独显加载中", null, null)
            : new GpuReading(
                discrete.IsAvailable,
                discrete.Name,
                discrete.ReadUsagePercent(),
                discrete.ReadTemperatureCelsius());
        var network = SampleNetwork(preferredAdapterId);
        if (!string.IsNullOrWhiteSpace(network.AdapterId))
        {
            _activeAdapterId = network.AdapterId;
        }

        return new SystemSnapshot(
            cpuPercent,
            memPercent,
            usedGb,
            totalGb,
            temp,
            integratedGpu,
            discreteGpu,
            network.UploadBytesPerSecond,
            network.DownloadBytesPerSecond,
            network.AdapterName,
            network.AdapterDetail,
            DateTimeOffset.Now,
            _cpuName,
            LogicalProcessors);
    }

    private void SampleCpu(out double percent)
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            percent = 0;
            return;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        if (!_cpuInitialized)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _cpuInitialized = true;
            percent = 0;
            return;
        }

        var idleDelta = idle >= _lastIdle ? (double)(idle - _lastIdle) : 0;
        var kernelDelta = kernel >= _lastKernel ? (double)(kernel - _lastKernel) : 0;
        var userDelta = user >= _lastUser ? (double)(user - _lastUser) : 0;
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            percent = 0;
            return;
        }

        // The kernel bucket includes idle time.
        var busy = total - idleDelta;
        percent = Math.Clamp(busy / total * 100.0, 0, 100);
    }

    private static void SampleMemory(out double percent, out double usedGb, out double totalGb)
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            percent = 0;
            usedGb = 0;
            totalGb = 0;
            return;
        }

        totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1024d / 1024d / 1024d;
        percent = status.dwMemoryLoad;
    }

    private (string AdapterId, string AdapterName, string AdapterDetail, double UploadBytesPerSecond, double DownloadBytesPerSecond)
        SampleNetwork(string? preferredAdapterId)
    {
        NetworkInterface? nic;
        NetworkAdapterInfo? info;
        try
        {
            var adapters = NetworkSampler.GetAdapters();
            var preferred = string.IsNullOrWhiteSpace(preferredAdapterId)
                ? NetworkSampler.PickDefault(adapters)
                : adapters.FirstOrDefault(a => a.Id == preferredAdapterId) ?? NetworkSampler.PickDefault(adapters);
            info = preferred;
            nic = info is null ? null : NetworkSampler.FindInterface(info.Id);
        }
        catch
        {
            return (string.Empty, "无网络", "未检测到活动网卡", 0, 0);
        }

        if (info is null || nic is null)
        {
            return (string.Empty, "无网络", "未检测到活动网卡", 0, 0);
        }

        if (_activeAdapterId != info.Id)
        {
            _activeAdapterId = info.Id;
            _lastBytesSent = 0;
            _lastBytesReceived = 0;
            _lastNetworkTimestamp = 0;
        }

        try
        {
            var stats = nic.GetIPStatistics();
            var sent = stats.BytesSent;
            var received = stats.BytesReceived;
            var now = _clock.ElapsedMilliseconds;

            double up = 0;
            double down = 0;
            if (_lastNetworkTimestamp > 0 && now > _lastNetworkTimestamp)
            {
                var seconds = (now - _lastNetworkTimestamp) / 1000.0;
                up = Math.Max(0, sent - _lastBytesSent) / seconds;
                down = Math.Max(0, received - _lastBytesReceived) / seconds;
            }

            _lastBytesSent = sent;
            _lastBytesReceived = received;
            _lastNetworkTimestamp = now;

            var detail = $"{info.TypeLabel} · 本机实时采集 · 仅统计所选网卡";
            return (info.Id, info.Name, detail, up, down);
        }
        catch
        {
            return (info.Id, info.Name, info.Description, 0, 0);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var value = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var name = value.Trim();
                var cut = name.IndexOf(" w/ ", StringComparison.OrdinalIgnoreCase);
                if (cut > 0) name = name[..cut];
                return name.Trim();
            }
        }
        catch
        {
            // Fall through to a generic label.
        }

        return "本机处理器";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _thermal?.Dispose();
            _gpu?.Dispose();
            _integratedGpu?.Dispose();
            _thermal = null;
            _gpu = null;
            _integratedGpu = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    private static class NetworkSampler
    {
        public static IReadOnlyList<NetworkAdapterInfo> GetAdapters()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.Ppp
                    or NetworkInterfaceType.Tunnel)
                .Select(n => new NetworkAdapterInfo(
                    n.Id,
                    n.Name,
                    n.Description,
                    n.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN",
                        _ => "以太网"
                    },
                    n.OperationalStatus == OperationalStatus.Up))
                .OrderByDescending(a => a.IsConnected)
                .ThenBy(a => a.TypeLabel == "以太网" ? 0 : 1)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static NetworkAdapterInfo? PickDefault(IReadOnlyList<NetworkAdapterInfo> adapters)
        {
            var connected = adapters.Where(a => a.IsConnected).ToList();
            if (connected.Count == 0) return adapters.FirstOrDefault();

            NetworkAdapterInfo? best = null;
            var bestScore = int.MinValue;
            foreach (var adapter in connected)
            {
                var score = adapter.TypeLabel == "以太网" ? 40 : 20;
                try
                {
                    var nic = FindInterface(adapter.Id);
                    if (nic is not null)
                    {
                        var properties = nic.GetIPProperties();
                        if (properties.GatewayAddresses.Any(g =>
                                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !g.Address.Equals(IPAddress.Any)))
                        {
                            score += 40;
                        }

                        var speed = nic.Speed;
                        if (speed > 0) score += (int)Math.Min(20, Math.Log10(speed));
                    }
                }
                catch
                {
                    // Keep the basic score.
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = adapter;
                }
            }

            return best ?? connected[0];
        }

        public static NetworkInterface? FindInterface(string id) =>
            NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);
    }

    public static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond)) return "0 B/s";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0.0} KB/s";
        if (bytesPerSecond < 1024d * 1024d * 1024d) return $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s";
        return $"{bytesPerSecond / 1024d / 1024d / 1024d:0.00} GB/s";
    }

    public static string FormatMemoryGb(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
