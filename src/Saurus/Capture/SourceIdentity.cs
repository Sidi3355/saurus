using System.IO;
using System.Text.RegularExpressions;

namespace Saurus.Capture;

/// <summary>
/// Answers "what am I reading?" as a stable key.
///
/// This is the load-bearing part of threading. A thread is a document, not a time window:
/// you can read one paper across three days, switch between four of them in an afternoon,
/// and every lookup should land in the right conversation. Getting that right is almost
/// entirely a question of identifying the source reliably — semantics barely enters into it.
///
/// Resolution order, most specific first:
///   1. URL, canonicalised (arXiv IDs and DOIs collapse to a single key)
///   2. a local document path or filename
///   3. process + normalised document title
///   4. process alone — <see cref="IsCoarse"/>, and the caller must fall back to a time window
///
/// The raw evidence is kept alongside the key so that when these rules improve, historical
/// threads can be re-keyed rather than orphaned.
/// </summary>
public sealed partial record SourceIdentity(string Key, string Label, string Kind)
{
    /// <summary>
    /// True when the key identifies an application rather than a document. Threading on a
    /// coarse key with no time limit would put every lookup you have ever made in, say, a
    /// code editor into one enormous thread.
    /// </summary>
    public bool IsCoarse => Kind == "app";

    // ------------------------------------------------------------------ resolution

    public static SourceIdentity Resolve(ForegroundInfo fg)
    {
        if (!string.IsNullOrWhiteSpace(fg.Url))
        {
            var fromUrl = FromUrl(fg.Url!);
            if (fromUrl is not null) return fromUrl;
        }

        var title = NormalizeTitle(fg.WindowTitle, fg.ProcessName);

        // A document filename in the title is a strong identity even with no URL: it is how
        // every PDF viewer, word processor and editor announces what it has open.
        var file = DocumentFile().Match(title);
        if (file.Success)
        {
            var name = file.Value.Trim().ToLowerInvariant();
            var arxiv = ArxivFile().Match(name);
            if (arxiv.Success)
                return new SourceIdentity($"arxiv:{arxiv.Groups[1].Value}", name, "file");

            return new SourceIdentity($"doc:{name}", name, "file");
        }

        if (!string.IsNullOrWhiteSpace(title) &&
            !title.Equals(fg.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            title.Length >= 3)
        {
            var proc = fg.ProcessName.ToLowerInvariant();
            return new SourceIdentity($"win:{proc}|{title.ToLowerInvariant()}", title, "window");
        }

        var app = string.IsNullOrEmpty(fg.ProcessName) ? "unknown" : fg.ProcessName;
        return new SourceIdentity($"app:{app.ToLowerInvariant()}", app, "app");
    }

    // ------------------------------------------------------------------ urls

    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "fbclid", "gclid", "msclkid", "mc_cid", "mc_eid", "igshid", "si",
        "ref", "ref_src", "referrer", "_hsenc", "_hsmi", "spm", "scrlybrkr"
    };

    private static SourceIdentity? FromUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return null;

        // A PDF opened in a browser arrives as file:/// - treat it as the local document it is.
        if (u.IsFile) return FromPath(u.LocalPath);

        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;

        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        var path = u.AbsolutePath.TrimEnd('/');

        // The same paper reached three ways - the abstract page, the PDF, and a downloaded
        // copy - is one document and belongs in one thread. This is the case that makes
        // document threading actually work for reading papers.
        if (host.EndsWith("arxiv.org", StringComparison.Ordinal))
        {
            var m = ArxivPath().Match(path);
            if (m.Success) return new SourceIdentity($"arxiv:{m.Groups[1].Value}", $"arXiv {m.Groups[1].Value}", "url");
        }

        if (host is "doi.org" or "dx.doi.org")
        {
            var doi = path.Trim('/').ToLowerInvariant();
            if (doi.Length > 0) return new SourceIdentity($"doi:{doi}", $"doi:{doi}", "url");
        }

        // Everything else: host + path, with tracking noise removed but real query
        // parameters kept, because plenty of documents are identified by one.
        var kept = new List<string>();
        if (!string.IsNullOrEmpty(u.Query))
        {
            foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Split('=')[0];
                if (!TrackingParams.Contains(name)) kept.Add(part);
            }
            kept.Sort(StringComparer.Ordinal);   // parameter order is not identity
        }

        var key = $"url:{host}{path.ToLowerInvariant()}";
        if (kept.Count > 0) key += "?" + string.Join("&", kept);

        var label = string.IsNullOrEmpty(path) ? host : host + path;
        if (label.Length > 60) label = label[..60] + "…";

        return new SourceIdentity(key, label, "url");
    }

    private static SourceIdentity FromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        name = name.ToLowerInvariant();

        var arxiv = ArxivFile().Match(name);
        if (arxiv.Success)
            return new SourceIdentity($"arxiv:{arxiv.Groups[1].Value}", name, "file");

        // Full path, not bare filename: two different papers both called "paper.pdf" in
        // different folders must not collapse into one thread.
        return new SourceIdentity($"file:{path.ToLowerInvariant()}", name, "file");
    }

    // ------------------------------------------------------------------ titles

    /// <summary>Application names that appear as a trailing segment of a window title.</summary>
    private static readonly string[] AppSuffixes =
    {
        "Google Chrome", "Mozilla Firefox", "Microsoft Edge", "Brave", "Vivaldi", "Opera",
        "Adobe Acrobat Reader", "Adobe Acrobat", "SumatraPDF", "Foxit PDF Reader", "Foxit Reader",
        "Visual Studio Code", "Visual Studio", "Zotero", "Obsidian", "Notepad++", "Notepad",
        "Microsoft Word", "Word", "Microsoft Excel", "Excel", "Microsoft PowerPoint", "PowerPoint",
        "Mendeley", "Sioyek", "Okular", "Preview"
    };

    /// <summary>
    /// Strips the noise that changes while the document does not: unread counts, dirty
    /// markers, page counters, and the trailing application name. A PDF viewer that puts
    /// "4 of 31" in the title would otherwise start a new thread on every page turn.
    /// </summary>
    public static string NormalizeTitle(string? rawTitle, string processName)
    {
        var t = rawTitle ?? "";
        if (t.Length == 0) return "";

        t = t.Trim().TrimStart('●', '•', '*', '·', ' ', '​');

        // Leading "(3) " unread count.
        if (t.StartsWith('('))
        {
            var close = t.IndexOf(')');
            if (close > 1 && close < 6 && t[1..close].All(char.IsDigit)) t = t[(close + 1)..];
        }

        t = PageCounterSuffix().Replace(t, "");
        t = PageCounterParens().Replace(t, "");

        foreach (var app in AppSuffixes)
        {
            foreach (var sep in new[] { " - ", " — ", " – ", " | " })
            {
                var tail = sep + app;
                if (t.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    t = t[..^tail.Length];
                    break;
                }
            }
        }

        t = t.Trim(' ', '-', '—', '–', '|');

        // Some apps use the bare process name as the title when nothing is open.
        if (t.Equals(processName, StringComparison.OrdinalIgnoreCase)) return t;

        return t;
    }

    // ------------------------------------------------------------------ patterns

    [GeneratedRegex(@"[\w\-.,'()\[\] ]+\.(?:pdf|docx?|epub|pptx?|xlsx?|djvu|tex|md|txt|csv|rtf)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentFile();

    [GeneratedRegex(@"^(\d{4}\.\d{4,5})(?:v\d+)?\.pdf$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArxivFile();

    [GeneratedRegex(@"/(?:abs|pdf|html)/(\d{4}\.\d{4,5})(?:v\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArxivPath();

    /// <summary>" - 4 of 31", " | 4/31", " — Page 4 of 31" at the end of a title.</summary>
    [GeneratedRegex(@"\s*[-–—|]\s*(?:page\s+)?\d+\s*(?:of|/)\s*\d+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageCounterSuffix();

    /// <summary>"(4 of 31)" or "(4/31)" at the end of a title.</summary>
    [GeneratedRegex(@"\s*\(\s*\d+\s*(?:of|/)\s*\d+\s*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageCounterParens();
}
