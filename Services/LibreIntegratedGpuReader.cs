using LibreHardwareMonitor.Hardware;

namespace LocalDeck.Services;

/// <summary>
/// Read-only integrated-GPU reader. Windows exposes iGPU utilization through
/// the D3D performance counters collected by LibreHardwareMonitor; the AMD
/// Radeon 780M driver does not expose an independent GPU temperature sensor.
/// </summary>
public sealed class LibreIntegratedGpuReader : IDisposable
{
    private Computer? _computer;
    private IHardware? _gpu;
    private readonly string _name = "核显不可用";

    public LibreIntegratedGpuReader()
    {
        try
        {
            var computer = new Computer { IsGpuEnabled = true };
            computer.Open();
            _computer = computer;

            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    _gpu = hardware;
                    _name = string.IsNullOrWhiteSpace(hardware.Name) ? "核显" : hardware.Name.Trim();
                    break;
                }
            }
        }
        catch
        {
            _computer?.Close();
            _computer = null;
            _gpu = null;
        }
    }

    public bool IsAvailable => _gpu is not null;

    public string Name => _name;

    public double? ReadUsagePercent()
    {
        if (_gpu is null) return null;

        try
        {
            _gpu.Update();
            var loads = _gpu.Sensors
                .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue)
                .ToList();

            var primary = loads.FirstOrDefault(s =>
                string.Equals(s.Name, "D3D 3D", StringComparison.OrdinalIgnoreCase));
            if (primary?.Value is float primaryValue)
            {
                return Math.Clamp(primaryValue, 0, 100);
            }

            // Some integrated drivers name the 3D engine differently.
            var d3d = loads
                .Where(s => s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Value!.Value)
                .DefaultIfEmpty(0)
                .Max();
            return d3d > 0 ? Math.Clamp(d3d, 0, 100) : 0;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _computer?.Close();
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _computer = null;
        _gpu = null;
    }
}
