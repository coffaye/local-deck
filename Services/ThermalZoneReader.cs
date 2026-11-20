using System.Runtime.InteropServices;

namespace LocalDeck.Services;

/// <summary>
/// Reads the Windows ACPI thermal-zone performance counter without requiring elevation.
/// On many laptops this is the best non-admin temperature reading available.
/// </summary>
public sealed class ThermalZoneReader : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter;
    private bool _opened;
    private double? _lastValue;

    public ThermalZoneReader()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;

            // PdhAddEnglishCounter works even on localized Windows installations.
            var status = PdhAddEnglishCounterW(_query, @"\Thermal Zone Information(*)\Temperature",
                IntPtr.Zero, out _counter);
            if (status != 0) return;

            PdhCollectQueryData(_query);
            _opened = true;
        }
        catch
        {
            _opened = false;
        }
    }

    public double? ReadCelsius()
    {
        if (!_opened) return _lastValue;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return _lastValue;

            var bufferSize = 0u;
            var itemCount = 0u;
            var status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble,
                ref bufferSize, out itemCount, IntPtr.Zero);

            if (status != PdhMoreData || bufferSize == 0 || itemCount == 0) return _lastValue;

            var buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble,
                    ref bufferSize, out itemCount, buffer);
                if (status != 0 || itemCount == 0) return _lastValue;

                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                double? best = null;
                for (var i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                    // 0 = PDH_CSTATUS_VALID_DATA, 1 = PDH_CSTATUS_NEW_DATA.
                    if (item.Value.CStatus is not (0 or 1)) continue;

                    var value = Normalize(item.Value.DoubleValue);
                    if (value is null) continue;
                    best = best is null ? value : Math.Max(best.Value, value.Value);
                }

                if (best is not null) _lastValue = best;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Keep the last good reading.
        }

        return _lastValue;
    }

    private static double? Normalize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return null;

        // The performance counter is documented in Kelvin on most systems, but
        // some vendor stacks expose Celsius directly.
        var celsius = value > 180 ? value - 273.15 : value;
        return celsius is > 5 and < 125 ? celsius : null;
    }

    public void Dispose()
    {
        if (_counter != IntPtr.Zero)
        {
            PdhRemoveCounter(_counter);
            _counter = IntPtr.Zero;
        }

        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        _opened = false;
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath,
        IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
        ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhRemoveCounter(IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
