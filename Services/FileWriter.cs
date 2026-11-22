using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsNotch.Services;

/// <summary>
/// Saves what the notch wrote for you (a letter, notes, an essay, code…) where you asked: "on my Desktop",
/// "in Documents\Letters", "in Downloads", or a full path. Word files (.docx) are built directly (no Office needed);
/// everything else is plain UTF-8 text.
/// Safety: never overwrites a file unless asked (a "(2)" copy is made instead), never writes into Windows or Program
/// Files, and never creates programs or scripts that could run by double-click.
/// </summary>
public static class FileWriter
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".msi", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".jse", ".wsf", ".wsh",
        ".lnk", ".url", ".reg", ".hta", ".cpl", ".sys", ".inf", ".jar", ".appx", ".msix",
    };

    /// <summary>Where to save: a known folder name, a path under your user folder, or a full path.</summary>
    public static string ResolveFolder(string? folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Known(string name) => name.ToLowerInvariant() switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" or "my documents" or "docs" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" => Path.Combine(home, "Downloads"),
            "pictures" or "photos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "home" or "my folder" or "user" => home,
            _ => "",
        };

        var f = (folder ?? "").Trim().Trim('"').Replace('/', '\\');
        f = Regex.Replace(f, @"^(my|the)\s+", "", RegexOptions.IgnoreCase);
        if (f.Length == 0) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Notch");
        if (Path.IsPathRooted(f)) return Path.GetFullPath(f);
        // "Documents\Letters", "desktop\work notes"
        var first = f.Split('\\')[0];
        var known = Known(first);
        if (known.Length > 0) return Path.GetFullPath(Path.Combine(known, f.Length > first.Length ? f[(first.Length + 1)..] : ""));
        var whole = Known(f);
        if (whole.Length > 0) return whole;
        // a plain name ("Letters") → a folder of that name in Documents
        return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), f));
    }

    private static bool IsProtected(string dir)
    {
        var d = dir.TrimEnd('\\') + "\\";
        string[] off =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };
        return off.Where(o => !string.IsNullOrEmpty(o)).Any(o => d.StartsWith(o.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
               || string.Equals(d, Path.GetPathRoot(d), StringComparison.OrdinalIgnoreCase) && d.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the file; returns its full path, or throws with a short, speakable reason.</summary>
    public static string Save(string content, string? fileName, string? folder, string? format, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("There was nothing to write.");
        var dir = ResolveFolder(folder);
        if (IsProtected(dir)) throw new InvalidOperationException("That folder belongs to Windows or installed programs; pick Documents, Desktop or another folder of yours.");

        var name = string.Concat((fileName ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Notch note " + DateTime.Now.ToString("yyyy-MM-dd HHmm");
        var ext = Path.GetExtension(name);
        var fmt = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0 || ext.Length > 6) { ext = "." + (fmt.Length > 0 ? fmt : "docx"); name += ext; }
        if (Blocked.Contains(ext)) throw new InvalidOperationException($"I don't create {ext} files, because they could run as programs.");

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (File.Exists(path) && !overwrite)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
        }

        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) WriteDocx(path, content);
        else File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        App.Log($"voice: wrote {path} ({content.Length} chars)");
        return path;
    }

    /// <summary>A small, valid Word document: one paragraph per line; "# Title" lines become headings, **bold** stays bold.</summary>
    private static void WriteDocx(string path, string content)
    {
        var body = new StringBuilder();
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = 0;
            var m = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
            if (m.Success) { heading = m.Groups[1].Value.Length; line = m.Groups[2].Value; }
            var bullet = Regex.Match(line, @"^\s*[-*•]\s+(.*)$");
            if (bullet.Success) line = "•  " + bullet.Groups[1].Value;

            body.Append("<w:p>");
            var size = heading switch { 1 => 36, 2 => 30, 3 => 26, _ => 0 };
            body.Append("<w:pPr><w:spacing w:after=\"").Append(heading > 0 ? 160 : 120).Append("\"/></w:pPr>");
            // **bold** runs
            foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*)"))
            {
                if (part.Length == 0) continue;
                var bold = heading > 0 || (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4);
                var text = part.StartsWith("**") && part.EndsWith("**") && part.Length > 4 ? part[2..^2] : part;
                body.Append("<w:r><w:rPr>");
                if (bold) body.Append("<w:b/>");
                if (size > 0) body.Append("<w:sz w:val=\"").Append(size).Append("\"/>");
                body.Append("</w:rPr><w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(text)).Append("</w:t></w:r>");
            }
            body.Append("</w:p>");
        }

        const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";
        var doc = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>";

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Add("[Content_Types].xml", types);
        Add("_rels/.rels", rels);
        Add("word/document.xml", doc);
    }

    /// <summary>Opens a file the notch wrote (or a folder) in its normal app. Programs are never started this way.</summary>
    public static string Open(string path)
    {
        var p = path.Trim().Trim('"');
        if (!File.Exists(p) && !Directory.Exists(p)) throw new InvalidOperationException("That file or folder doesn't exist.");
        if (File.Exists(p) && Blocked.Contains(Path.GetExtension(p))) throw new InvalidOperationException("I don't open programs or scripts.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
        return p;
    }
}
