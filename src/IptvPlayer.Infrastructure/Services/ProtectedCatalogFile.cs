using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IptvPlayer.Infrastructure.Services;

internal static class ProtectedCatalogFile
{
    private static readonly byte[] Magic = "WIPTCAT1"u8.ToArray();
    private const int CryptProtectUiForbidden = 0x1;

    public static bool IsProtected(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < Magic.Length)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[Magic.Length];
        return stream.Read(header) == Magic.Length && header.SequenceEqual(Magic);
    }

    public static byte[] Read(string path)
    {
        var envelope = File.ReadAllBytes(path);
        try
        {
            return UnprotectEnvelope(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    public static void WriteAtomic(string path, ReadOnlySpan<byte> payload)
    {
        var envelope = CreateEnvelope(payload);
        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tempPath, envelope);
            Verify(tempPath, payload);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            TryDeleteTemporaryFile(tempPath);
        }
    }

    public static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var envelope = CreateEnvelope(payload.Span);
        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(tempPath, envelope, cancellationToken).ConfigureAwait(false);
            Verify(tempPath, payload.Span);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static byte[] CreateEnvelope(ReadOnlySpan<byte> payload)
    {
        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var gzip = new GZipStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(payload);
            }

            compressed = compressedStream.ToArray();
        }

        try
        {
            var protectedPayload = ProtectForCurrentUser(compressed);
            try
            {
                var envelope = new byte[Magic.Length + protectedPayload.Length];
                Magic.CopyTo(envelope, 0);
                protectedPayload.CopyTo(envelope, Magic.Length);
                return envelope;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    private static byte[] UnprotectEnvelope(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length <= Magic.Length || !envelope[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The local data file does not contain a recognized protected envelope.");
        }

        var compressed = UnprotectForCurrentUser(envelope[Magic.Length..]);
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    private static void Verify(string path, ReadOnlySpan<byte> expectedPayload)
    {
        var actualPayload = Read(path);
        try
        {
            var expectedHash = SHA256.HashData(expectedPayload);
            var actualHash = SHA256.HashData(actualPayload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                {
                    throw new CryptographicException("The protected local data file failed round-trip verification.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedHash);
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualPayload);
        }
    }

    private static byte[] ProtectForCurrentUser(ReadOnlySpan<byte> value)
        => TransformWithDpapi(value, protect: true);

    private static byte[] UnprotectForCurrentUser(ReadOnlySpan<byte> value)
        => TransformWithDpapi(value, protect: false);

    private static byte[] TransformWithDpapi(ReadOnlySpan<byte> value, bool protect)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Data Protection is required for protected local data.");
        }

        var input = new DataBlob();
        var output = new DataBlob();
        var managedInput = value.ToArray();
        try
        {
            input.Size = value.Length;
            input.Data = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(managedInput, 0, input.Data, value.Length);

            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    "Whose IPTV local data",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);

            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(managedInput);

            if (input.Data != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }

            if (output.Data != IntPtr.Zero)
            {
                ZeroUnmanagedMemory(output.Data, output.Size);
                _ = LocalFree(output.Data);
            }
        }
    }

    private static void ZeroUnmanagedMemory(IntPtr address, int length)
    {
        const int blockSize = 4096;
        var zeros = new byte[Math.Min(blockSize, Math.Max(0, length))];
        var offset = 0;
        while (offset < length)
        {
            var count = Math.Min(zeros.Length, length - offset);
            Marshal.Copy(zeros, 0, IntPtr.Add(address, offset), count);
            offset += count;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
