using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeYouTubeConverterStudio.Services;

public sealed record DenoInstallResult(bool Ok, string Message);

/// <summary>
/// Detects and installs Deno (the JavaScript runtime yt-dlp uses for YouTube) via
/// <c>winget install DenoLand.Deno</c>, and refreshes this process's PATH so yt-dlp finds it
/// immediately, with no app restart.
/// </summary>
public static class DenoService
{
    // winget exit codes that mean "nothing to do, it's already there".
    private const int AlreadyInstalled = unchecked((int)0x8A150061);
    private const int NoApplicableUpgrade = unchecked((int)0x8A15002B);

    public static string? Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "deno.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        // winget puts portable-package shims here.
        var links = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "deno.exe");
        return File.Exists(links) ? links : null;
    }

    /// <summary>
    /// Re-reads the machine and user PATH from the registry into this process. Installers update
    /// the registry only; processes started earlier (like this app) keep their old PATH.
    /// </summary>
    public static void RefreshPath()
    {
        try
        {
            var entries = new List<string>();

            void AddAll(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                foreach (var raw in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = Environment.ExpandEnvironmentVariables(raw.Trim());
                    if (e.Length > 0 && !entries.Contains(e, StringComparer.OrdinalIgnoreCase))
                        entries.Add(e);
                }
            }

            AddAll(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            AddAll(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddAll(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process));

            var links = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links");
            if (Directory.Exists(links) && !entries.Contains(links, StringComparer.OrdinalIgnoreCase))
                entries.Add(links);

            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entries), EnvironmentVariableTarget.Process);
        }
        catch { /* non-fatal */ }
    }

    public static async Task<DenoInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Equivalent to: winget install DenoLand.Deno
        foreach (var a in new[]
        {
            "install", "--id", "DenoLand.Deno", "--exact", "--source", "winget",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"
        })
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("winget could not be started.");
        }
        catch (Win32Exception)
        {
            return new DenoInstallResult(false,
                "winget isn't available on this PC. Install “App Installer” from the Microsoft Store, or install Deno manually from deno.com.");
        }
        catch (Exception ex)
        {
            return new DenoInstallResult(false, ex.Message);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
                throw;
            }

            var output = Summarize(await stdout + "\n" + await stderr);
            var code = process.ExitCode;

            RefreshPath();
            var found = Find();

            if (code == 0 || code == AlreadyInstalled || code == NoApplicableUpgrade || found != null)
            {
                return new DenoInstallResult(true, found != null
                    ? $"Deno is ready ({found}). yt-dlp will use it right away — no restart needed."
                    : "Installed. If downloads still complain about a JavaScript runtime, restart the app.");
            }

            return new DenoInstallResult(false,
                $"winget exited with code 0x{code:X8}." + (output.Length > 0 ? $"  {output}" : ""));
        }
    }

    /// <summary>Strips winget's spinner/progress noise and keeps the last few meaningful lines.</summary>
    private static string Summarize(string text)
    {
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Length > 1 && !Regex.IsMatch(l, @"^[\s\-\\|/█▒░]+$") && !Regex.IsMatch(l, @"^\d+(\.\d+)?\s*(KB|MB|GB)\s*/"))
            .TakeLast(3);

        return string.Join(" ", lines);
    }
}
