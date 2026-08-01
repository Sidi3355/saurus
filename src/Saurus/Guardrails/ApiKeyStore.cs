using System.IO;
using System.Security.Cryptography;
using System.Text;
using Saurus.Core;

namespace Saurus.Guardrails;

/// <summary>
/// API key at rest, encrypted with Windows DPAPI scoped to the current user.
///
/// What that actually buys you: another user account on this machine cannot read the file,
/// and neither can anyone who mounts the drive offline. What it does not buy you: any
/// process running as you can call CryptUnprotectData on the same blob and recover the
/// key — that is exactly what SAURUS does at startup. It defeats disk theft and other
/// users, not malware in your own session.
///
/// The key never touches the repo, the config file, or the log.
/// </summary>
public static class ApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("saurus.apikey.v1");

    public static string PathFor(string dir) => Path.Combine(dir, "apikey.dat");

    public static bool Exists(string dir) => File.Exists(PathFor(dir));

    public static void Save(string dir, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is empty", nameof(apiKey));

        Directory.CreateDirectory(dir);
        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Array.Clear(plain);

        var path = PathFor(dir);
        File.WriteAllBytes(path, blob);

        // Not a security boundary on its own (DPAPI is), but it keeps the file out of
        // casual directory listings and backup tools that skip hidden files.
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch { }

        Log.Info($"api key stored (DPAPI, CurrentUser) at {path}");
    }

    /// <summary>Returns null when absent or undecryptable. Never logs the value.</summary>
    public static string? Load(string dir)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return null;

        try
        {
            var blob = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            Array.Clear(plain);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (CryptographicException)
        {
            // Written under a different Windows user, or the profile was rebuilt.
            Log.Error("api key blob could not be decrypted for this user; re-run --set-key");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("api key could not be read", ex);
            return null;
        }
    }

    public static void Delete(string dir)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return;
        try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        File.Delete(path);
        Log.Info("api key deleted");
    }

    /// <summary>Shape check only — no network call. Used to give a useful setup error early.</summary>
    public static bool LooksPlausible(string key) =>
        key.StartsWith("sk-ant-", StringComparison.Ordinal) && key.Length > 20;
}
