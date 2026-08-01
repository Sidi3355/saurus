using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Saurus.Core;

/// <summary>
/// Local rolling log. Everything written goes through <see cref="Redact"/> first —
/// the API key must never reach disk in plaintext, including via an exception message
/// or a serialized request body.
/// </summary>
public static partial class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string? _secret;      // the live API key, registered so we can scrub it
    private const long MaxBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9\-_]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    public static void Init(string dir)
    {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "saurus.log");
        Roll();
        Info("--- saurus start ---");
    }

    /// <summary>
    /// Registers the live key so any accidental appearance in a log line is scrubbed.
    /// The key itself is held only to compare against; it is never written.
    /// </summary>
    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret)) _secret = secret;
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Error(string msg, Exception ex) =>
        Write("ERR ", $"{msg}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {Redact(msg)}";
        lock (Gate)
        {
            try
            {
                if (_path is null) { System.Diagnostics.Debug.WriteLine(line); return; }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* logging must never take the app down */ }
        }
    }

    /// <summary>
    /// Strips the registered key and anything shaped like an Anthropic key.
    /// Applied to every line, including exception text.
    /// </summary>
    public static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (_secret is not null && s.Contains(_secret, StringComparison.Ordinal))
            s = s.Replace(_secret, "<redacted-key>", StringComparison.Ordinal);
        return ApiKeyPattern().Replace(s, "<redacted-key>");
    }

    /// <summary>
    /// Short stable hash for logging text we do not want to store verbatim.
    /// Used instead of selection/context bodies unless logSelections is on.
    /// </summary>
    public static string Fingerprint(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    private static void Roll()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            if (new FileInfo(_path).Length < MaxBytes) return;
            var old = _path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(_path, old);
        }
        catch { /* ignore */ }
    }
}
