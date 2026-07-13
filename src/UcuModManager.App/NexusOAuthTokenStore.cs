using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UcuModManager.Core.Nexus;
using UcuModManager.Core.Storage;

namespace UcuModManager.App;

internal sealed class NexusOAuthTokenStore
{
    private const int CryptProtectUiForbidden = 0x1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool HasTokens(ManagerPaths managerPaths)
    {
        return File.Exists(GetTokenPath(managerPaths));
    }

    public NexusOAuthTokenSet? LoadTokens(ManagerPaths managerPaths)
    {
        var path = GetTokenPath(managerPaths);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var json = Encoding.UTF8.GetString(Unprotect(protectedBytes));
        return JsonSerializer.Deserialize<NexusOAuthTokenSet>(json, JsonOptions);
    }

    public void SaveTokens(ManagerPaths managerPaths, NexusOAuthTokenSet tokens)
    {
        var path = GetTokenPath(managerPaths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(tokens, JsonOptions);
        File.WriteAllBytes(path, Protect(Encoding.UTF8.GetBytes(json)));
    }

    public void ClearTokens(ManagerPaths managerPaths)
    {
        var path = GetTokenPath(managerPaths);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetTokenPath(ManagerPaths managerPaths)
    {
        return Path.Combine(managerPaths.RootPath, "secrets", "nexus-oauth-tokens.dpapi");
    }

    private static byte[] Protect(byte[] bytes)
    {
        using var input = DataBlob.FromBytes(bytes);
        if (!CryptProtectData(input.Pointer, "UCU ModManager Nexus OAuth Tokens", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
        {
            throw new InvalidOperationException("Windows could not encrypt the Nexus OAuth tokens.");
        }

        using var protectedBlob = new DataBlob(output);
        return protectedBlob.ToArray();
    }

    private static byte[] Unprotect(byte[] bytes)
    {
        using var input = DataBlob.FromBytes(bytes);
        if (!CryptUnprotectData(input.Pointer, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
        {
            throw new InvalidOperationException("Windows could not decrypt the Nexus OAuth tokens for the current user.");
        }

        using var protectedBlob = new DataBlob(output);
        return protectedBlob.ToArray();
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        IntPtr pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        IntPtr pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DATA_BLOB
    {
        public readonly int cbData;
        public readonly IntPtr pbData;

        public DATA_BLOB(int cbData, IntPtr pbData)
        {
            this.cbData = cbData;
            this.pbData = pbData;
        }
    }

    private sealed class DataBlob : IDisposable
    {
        private readonly bool _ownsLocalAlloc;
        private bool _disposed;

        private DataBlob(DATA_BLOB blob, bool ownsLocalAlloc)
        {
            Blob = blob;
            _ownsLocalAlloc = ownsLocalAlloc;
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<DATA_BLOB>());
            Marshal.StructureToPtr(Blob, Pointer, false);
        }

        public DATA_BLOB Blob { get; }

        public IntPtr Pointer { get; }

        public static DataBlob FromBytes(byte[] bytes)
        {
            var dataPointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, dataPointer, bytes.Length);
            return new DataBlob(new DATA_BLOB(bytes.Length, dataPointer), ownsLocalAlloc: false);
        }

        public DataBlob(DATA_BLOB blob)
            : this(blob, ownsLocalAlloc: true)
        {
        }

        public byte[] ToArray()
        {
            if (Blob.cbData <= 0 || Blob.pbData == IntPtr.Zero)
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[Blob.cbData];
            Marshal.Copy(Blob.pbData, bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Marshal.FreeHGlobal(Pointer);
            if (Blob.pbData != IntPtr.Zero)
            {
                if (_ownsLocalAlloc)
                {
                    LocalFree(Blob.pbData);
                }
                else
                {
                    Marshal.FreeHGlobal(Blob.pbData);
                }
            }

            _disposed = true;
        }
    }
}
