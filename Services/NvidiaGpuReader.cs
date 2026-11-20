using System.Runtime.InteropServices;
using System.Text;

namespace LocalDeck.Services;

/// <summary>
/// Minimal NVIDIA NVML client. NVML is installed with the NVIDIA display driver
/// and works without administrator rights.
/// </summary>
public sealed class NvidiaGpuReader : IDisposable
{
    private IntPtr _device;
    private bool _initialized;
    private string _name = "NVIDIA GPU";
    private double? _lastUsage;
    private double? _lastTemperature;

    public NvidiaGpuReader()
    {
        var nvmlInitialized = false;
        try
        {
            if (NvmlInitV2() != 0) return;
            nvmlInitialized = true;
            if (NvmlDeviceGetCountV2(out var count) != 0 || count == 0)
            {
                NvmlShutdown();
                return;
            }

            IntPtr best = IntPtr.Zero;
            ulong bestMemory = 0;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlDeviceGetHandleByIndexV2(i, out var handle) != 0) continue;
                if (NvmlDeviceGetMemoryInfo(handle, out var memory) != 0) continue;
                if (memory.Total > bestMemory)
                {
                    bestMemory = memory.Total;
                    best = handle;
                }
            }

            if (best == IntPtr.Zero)
            {
                NvmlShutdown();
                return;
            }

            _device = best;
            _name = ReadName(best);
            _initialized = true;
        }
        catch
        {
            if (nvmlInitialized)
            {
                try { NvmlShutdown(); } catch { }
            }

            _initialized = false;
        }
    }

    public bool IsAvailable => _initialized;
    public string Name => _name;

    public double? ReadUsagePercent()
    {
        if (!_initialized) return _lastUsage;
        try
        {
            if (NvmlDeviceGetUtilizationRates(_device, out var utilization) == 0)
            {
                _lastUsage = utilization.Gpu;
            }
        }
        catch
        {
            // Return the last known value.
        }

        return _lastUsage;
    }

    public double? ReadTemperatureCelsius()
    {
        if (!_initialized) return _lastTemperature;
        try
        {
            if (NvmlDeviceGetTemperature(_device, 0, out var temperature) == 0)
            {
                _lastTemperature = temperature;
            }
        }
        catch
        {
            // Return the last known value.
        }

        return _lastTemperature;
    }

    private static string ReadName(IntPtr device)
    {
        try
        {
            var buffer = new byte[96];
            if (NvmlDeviceGetName(device, buffer, (uint)buffer.Length) == 0)
            {
                var text = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch
        {
            // Ignore and use the generic name.
        }

        return "NVIDIA GPU";
    }

    public void Dispose()
    {
        if (!_initialized) return;
        try
        {
            NvmlShutdown();
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _initialized = false;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlInitV2();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetCountV2(out uint deviceCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetHandleByIndexV2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
}

