using System.Runtime.InteropServices;
using System.Text;

namespace LocalDeck.Services;

/// <summary>
/// Small DPAPI wrapper. Secrets are protected for the current Windows user and never
/// written to disk in plaintext.
/// </summary>
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalDeck.credential.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = Transform(input, protect: true);
        return Convert.ToBase64String(output);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return string.Empty;
        var input = Convert.FromBase64String(protectedBase64);
        var output = Transform(input, protect: false);
        return Encoding.UTF8.GetString(output);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob(input);
        var entropyBlob = new DataBlob(Entropy);
        DataBlob outBlob = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref inBlob, "LocalDeck", ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob);

            if (!ok) throw new InvalidOperationException("DPAPI operation failed.");

            var result = new byte[outBlob.Length];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Length);
            return result;
        }
        finally
        {
            inBlob.Dispose();
            entropyBlob.Dispose();
            if (outBlob.Data != IntPtr.Zero)
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;

        public DataBlob(byte[] bytes)
        {
            Length = bytes.Length;
            Data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Data, bytes.Length);
        }

        public void Dispose()
        {
            if (Data != IntPtr.Zero)
            {
                LocalFree(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
