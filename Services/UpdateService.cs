using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsNotch.Services;

public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Available, Failed }

/// <summary>
/// Keeps the notch up to date from the project's GitHub releases:
/// check the latest release → download its zip → verify the SHA-256 GitHub publishes for it → unpack next to
/// nothing (a temp folder) → on "install", a tiny script waits for the notch to quit, copies the new files over
/// the old ones and starts it again. Your settings, shelf and to-dos live in %APPDATA% and are untouched.
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "nzizajules123/windows-notch";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsNotch-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public Version Current { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
    public string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";
    public UpdateState State { get; private set; } = UpdateState.Idle;
    /// <summary>Download progress 0..1 while <see cref="UpdateState.Downloading"/>.</summary>
    public double Progress { get; private set; }
    /// <summary>The newer release's tag ("v1.8.4"), once one is found.</summary>
    public string? NewTag { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    private string? _staged;
    private static string WorkDir => Path.Combine(Path.GetTempPath(), "WindowsNotch-update");
    private static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');
    private static string ExePath => Environment.ProcessPath ?? Path.Combine(InstallDir, "WindowsNotch.exe");

    /// <summary>Running from a developer build (bin\Debug, bin\Release): never overwrite it, only tell.</summary>
    public static bool IsDevBuild
    {
        get
        {
            var d = InstallDir.ToLowerInvariant();
            if (d.Contains(@"\bin\debug") || d.Contains(@"\bin\release")) return true;
            // a published copy inside the source tree (…\publish\WindowsNotch) is a dev copy too
            var dir = new DirectoryInfo(InstallDir);
            for (int i = 0; i < 3 && dir?.Parent != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.Parent!.FullName, "WindowsNotch.csproj"))) return true;
            return false;
        }
    }

    /// <summary>Can this copy replace its own files (not a dev build, folder writable)?</summary>
    public static bool CanSelfInstall
    {
        get
        {
            if (IsDevBuild) return false;
            try
            {
                var probe = Path.Combine(InstallDir, ".update-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }   // e.g. unzipped into Program Files without admin rights
        }
    }

    private void Set(UpdateState s, string? error = null)
    {
        State = s;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>Looks for a newer release; downloads + verifies it when <paramref name="download"/> and this copy can update itself.</summary>
    public async Task CheckAsync(bool download)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading || State == UpdateState.Ready) return;
        Set(UpdateState.Checking);
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith("v") || !Version.TryParse(tag[1..], out var latest)) { Set(UpdateState.UpToDate); return; }
            if (Normalize(latest) <= Normalize(Current)) { NewTag = null; Set(UpdateState.UpToDate); return; }

            NewTag = tag;
            App.Log($"update: {tag} is available (running {CurrentText})");
            string? url = null, digest = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.Equals($"WindowsNotch-{tag}-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            }
            if (!download || !CanSelfInstall || url == null || !url.StartsWith("https://github.com/" + Repo + "/releases/download/"))
            {
                Set(UpdateState.Available);
                return;
            }
            await DownloadAsync(tag, url, digest, size);
        }
        catch (Exception ex)
        {
            App.Log("update check: " + ex.Message);
            Set(UpdateState.Failed, "Couldn't reach GitHub");
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private async Task DownloadAsync(string tag, string url, string? digest, long size)
    {
        Directory.CreateDirectory(WorkDir);
        var zip = Path.Combine(WorkDir, $"WindowsNotch-{tag}.zip");
        var dest = Path.Combine(WorkDir, tag);
        Progress = 0;
        Set(UpdateState.Downloading);

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? size;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var fs = File.Create(zip);
            var buf = new byte[1 << 16];
            long got = 0; int n; var lastTick = 0.0;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n));
                got += n;
                if (total > 0)
                {
                    Progress = (double)got / total;
                    if (Progress - lastTick >= 0.02) { lastTick = Progress; Changed?.Invoke(); }
                }
            }
        }

        // only install what GitHub says it published
        if (digest is { } dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var fs = File.OpenRead(zip);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (hash != dg[7..].ToLowerInvariant())
            {
                File.Delete(zip);
                App.Log($"update: checksum mismatch for {tag}");
                Set(UpdateState.Failed, "Download didn't match — try again later");
                return;
            }
        }
        else if (size > 0 && new FileInfo(zip).Length != size)
        {
            File.Delete(zip);
            Set(UpdateState.Failed, "Download incomplete — try again later");
            return;
        }

        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        ZipFile.ExtractToDirectory(zip, dest);
        if (!File.Exists(Path.Combine(dest, "WindowsNotch.exe")))
        {
            Set(UpdateState.Failed, "The update package looks wrong");
            return;
        }
        File.Delete(zip);
        _staged = dest;
        App.Log($"update: {tag} downloaded and verified, ready to install");
        Set(UpdateState.Ready);
    }

    /// <summary>Quits the notch, swaps in the new files and starts the new version. Returns false if it couldn't start.</summary>
    public bool Install()
    {
        if (State != UpdateState.Ready || _staged == null || NewTag == null) return false;
        try
        {
            var pid = Environment.ProcessId;
            var script = Path.Combine(WorkDir, "apply-update.cmd");
            var log = Path.Combine(App.DataDir, "update.log");
            // wait for this notch to exit → copy the new files over → start the new version
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
                $"robocopy \"{_staged}\" \"{InstallDir}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS >> \"{log}\"",
                "if %ERRORLEVEL% GEQ 8 (",
                $"  echo update copy failed with %ERRORLEVEL% >> \"{log}\"",
                $"  start \"\" \"{ExePath}\"",
                "  exit /b 1",
                ")",
                $"start \"\" \"{ExePath}\" --updated {NewTag}",
                $"rmdir /s /q \"{_staged}\"",
                "exit /b 0"));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = WorkDir,
            });
            App.Log($"update: installing {NewTag}, restarting");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("update install: " + ex.Message);
            Set(UpdateState.Failed, "Couldn't start the update");
            return false;
        }
    }
}
