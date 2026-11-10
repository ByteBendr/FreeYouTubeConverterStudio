using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FreeYouTubeConverterStudio.Services;

public sealed record DependencyState(bool AllFound, DateTime CheckedAt);

/// <summary>
/// Stores the result of the last dependency check in hasDepends.bin next to the exe.
///
/// The content ("true"/"false" + time) is encrypted and authenticated with AES-GCM using a key derived
/// from the app and this PC. Any edit — or copying the file from another PC — makes it fail
/// authentication, and the app then ignores it and simply re-checks. The file is also marked
/// read-only and hidden. This is tamper-evident, not tamper-proof: the key lives inside the app.
/// </summary>
public static class DependencyStateFile
{
    public const string FileName = "hasDepends.bin";

    private static readonly byte[] Magic = { (byte)'F', (byte)'Y', (byte)'D', 1 };
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Preferred location: the folder of the exe.</summary>
    public static string PrimaryPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Used only if the exe folder isn't writable (e.g. installed under Program Files).</summary>
    public static string FallbackPath => Path.Combine(SettingsService.Folder, FileName);

    // ------------------------------------------------------------------

    /// <summary>Returns the stored result, or null if the file is missing, altered, or from another PC.</summary>
    public static DependencyState? Read()
    {
        foreach (var path in new[] { PrimaryPath, FallbackPath })
        {
            var state = TryRead(path);
            if (state != null) return state;
        }
        return null;
    }

    /// <summary>Writes the result. Returns the path it was written to.</summary>
    public static string Write(bool allFound)
    {
        var plaintext = Encoding.UTF8.GetBytes(
            $"{(allFound ? "true" : "false")}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(DeriveKey(), TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag, Magic);

        var blob = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(Magic, 0, blob, 0, Magic.Length);
        Buffer.BlockCopy(nonce, 0, blob, Magic.Length, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, Magic.Length + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, Magic.Length + NonceSize + TagSize, cipher.Length);

        try
        {
            WriteLocked(PrimaryPath, blob);
            RemoveIfPresent(FallbackPath);   // keep a single source of truth
            return PrimaryPath;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppLog.Info($"Cannot write {PrimaryPath} ({ex.Message}); using {FallbackPath}");
            Directory.CreateDirectory(SettingsService.Folder);
            WriteLocked(FallbackPath, blob);
            RemoveIfPresent(PrimaryPath);
            return FallbackPath;
        }
    }

    // ------------------------------------------------------------------

    private static DependencyState? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var blob = File.ReadAllBytes(path);
            if (blob.Length < Magic.Length + NonceSize + TagSize + 1) return null;
            if (!blob.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

            var nonce = blob.AsSpan(Magic.Length, NonceSize).ToArray();
            var tag = blob.AsSpan(Magic.Length + NonceSize, TagSize).ToArray();
            var cipher = blob.AsSpan(Magic.Length + NonceSize + TagSize).ToArray();
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(DeriveKey(), TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, Magic);   // throws if anything was altered

            var parts = Encoding.UTF8.GetString(plain).Split('|');
            if (parts.Length != 2) return null;
            if (parts[0] is not ("true" or "false")) return null;
            if (!long.TryParse(parts[1], out var unix)) return null;

            return new DependencyState(
                parts[0] == "true",
                DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime);
        }
        catch
        {
            return null;   // tampered, corrupt, or written on another PC
        }
    }

    private static void WriteLocked(string path, byte[] blob)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);   // clear read-only to overwrite
        File.WriteAllBytes(path, blob);
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
    }

    private static void RemoveIfPresent(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { /* ignore */ }
    }

    private static byte[] DeriveKey()
    {
        // App secret + per-PC id, so the file is meaningless on any other machine.
        return SHA256.HashData(Encoding.UTF8.GetBytes($"FreeYouTubeConverterStudio|deps|v1|{MachineId()}"));
    }

    private static string MachineId()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
        }
        catch { /* fall through */ }

        return Environment.MachineName;
    }
}
