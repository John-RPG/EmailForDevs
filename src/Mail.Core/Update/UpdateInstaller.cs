using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Mail.Core.Update;

/// <summary>Raised when a download does not match the digest GitHub published.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Downloads a release, proves it is what GitHub said it was, and swaps it in.
///
/// The swap is the dangerous part: a running executable cannot overwrite itself
/// on Windows, and a half-finished replacement leaves the user with no working
/// app. So the new build is staged and verified in full before anything is
/// touched, and the swap itself is handed to a short script that runs after this
/// process exits, keeping the old exe until the new one is in place.
/// </summary>
public sealed class UpdateInstaller(HttpClient http)
{
    /// <summary>
    /// Fetches the asset to a temporary file and verifies it.
    ///
    /// Verification is mandatory when the release carries a digest. A release
    /// without one cannot be verified at all, and is refused rather than
    /// installed on trust — the point of this step is that the bytes which ran
    /// through it are the bytes GitHub published.
    /// </summary>
    public async Task<string> DownloadAsync(
        ReleaseInfo release,
        string stagingDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (release.Sha256 is not { Length: 64 })
            throw new UpdateVerificationException(
                $"{release.Tag} publishes no SHA-256 digest, so the download cannot be " +
                "verified. Install it manually from the release page if you trust it.");

        Directory.CreateDirectory(stagingDirectory);
        var target = Path.Combine(stagingDirectory, release.AssetName);

        using (var response = await http.GetAsync(
                   release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.SizeBytes;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = File.Create(target);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (total > 0) progress?.Report((double)written / total);
            }
        }

        var actual = await Sha256Async(target, ct);
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Delete it: a file that failed verification should not be sitting
            // on disk where it could later be run by accident.
            TryDelete(target);
            throw new UpdateVerificationException(
                $"The download did not match the digest GitHub published for {release.Tag}. " +
                $"Expected {release.Sha256}, got {actual}. The file was discarded.");
        }
        return target;
    }

    /// <summary>Lower-case hex SHA-256 of a file, streamed rather than loaded.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Unpacks a verified zip and hands the swap to a detached script, then asks
    /// the caller to shut down.
    ///
    /// Done with a script rather than in-process because the file being replaced
    /// is the one executing: Windows holds a lock on it for as long as the
    /// process lives. The script waits for this process to exit, moves the
    /// current exe aside rather than deleting it, puts the new one in place, and
    /// restores the old one if the move fails — so a failure leaves a working
    /// app rather than none.
    /// </summary>
    /// <returns>The staged executable, for logging.</returns>
    public static string StageAndSwap(
        string verifiedZip, string currentExePath, string stagingDirectory)
    {
        var unpacked = Path.Combine(stagingDirectory, "unpacked");
        if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
        ZipFile.ExtractToDirectory(verifiedZip, unpacked);

        var name = Path.GetFileName(currentExePath);
        var staged = Path.Combine(unpacked, name);
        if (!File.Exists(staged))
        {
            // Fall back to whatever single exe the archive holds, so renaming the
            // asset's inner file does not break updating.
            var candidates = Directory.GetFiles(unpacked, "*.exe", SearchOption.AllDirectories);
            staged = candidates.Length == 1
                ? candidates[0]
                : throw new InvalidOperationException(
                    $"Expected {name} in the archive; found {candidates.Length} executables.");
        }

        var script = Path.Combine(stagingDirectory, "apply-update.cmd");
        File.WriteAllText(script, SwapScript(new SwapPlan(
            Environment.ProcessId, staged, currentExePath, stagingDirectory,
            LogPathFor(stagingDirectory))));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = stagingDirectory,
        });
        return staged;
    }

    /// <summary>
    /// Where the swap script records what it did. Beside the staging directory
    /// rather than in it, because the script deletes the staging directory when
    /// it succeeds.
    /// </summary>
    public static string LogPathFor(string stagingDirectory) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(stagingDirectory)) ?? stagingDirectory,
            "apply-update.log");

    /// <summary>What the swap script needs to know.</summary>
    /// <param name="Pid">The app process to wait for.</param>
    /// <param name="Staged">The verified new executable.</param>
    /// <param name="Target">The executable being replaced.</param>
    /// <param name="Staging">Deleted after a successful swap.</param>
    /// <param name="LogPath">Appended to, so each attempt leaves a record.</param>
    /// <param name="WaitSeconds">
    /// How long the app gets to exit on its own before it is forced closed.
    /// </param>
    /// <param name="Launch">
    /// The batch command that starts the app afterwards. Replaceable so tests can
    /// observe the relaunch without starting anything.
    /// </param>
    public sealed record SwapPlan(
        int Pid, string Staged, string Target, string Staging, string LogPath,
        int WaitSeconds = 30, string? Launch = null);

    /// <summary>
    /// The swap, as a batch file. It runs unsupervised after the app is gone,
    /// so it has to finish on its own whatever happens, and has to leave a
    /// working app behind: the new one if the swap succeeds, the old one if not.
    ///
    /// It used to wait for the app to exit with no limit, and to exit silently
    /// on failure without relaunching anything. An app that hung on shutdown
    /// therefore left the user with no window, no update and no clue. Now the
    /// wait is bounded and ends by forcing the app closed (safe: every database
    /// write is an atomic commit), every step is logged, and every exit path
    /// starts some version of the app.
    ///
    /// System tools are called by absolute path, since a PATH carrying Unix
    /// tools (Git's usr\bin, for one) can shadow find and timeout. The delay is
    /// ping rather than timeout, which refuses to run without a console.
    /// </summary>
    public static string SwapScript(SwapPlan plan)
    {
        // Batch expands %...% even inside quotes, so a literal % in a path
        // must be doubled or the path silently changes.
        static string Esc(string value) => value.Replace("%", "%%");

        var pid = plan.Pid;
        var target = Esc(plan.Target);
        var backup = Esc(plan.Target + ".old");
        var staged = Esc(plan.Staged);
        var staging = Esc(plan.Staging);
        var log = Esc(plan.LogPath);
        var launch = plan.Launch ?? $"start \"\" \"{target}\"";

        return $"""
            @echo off
            setlocal
            set "SYS=%SystemRoot%\System32"
            set "LOG={log}"
            echo.>>"%LOG%"
            echo %date% %time% Update: waiting for eeeMail pid {pid} to exit.>>"%LOG%"

            rem Wait for the app to exit before touching its exe - but not forever.
            set /a waited=0
            :wait
            "%SYS%\tasklist.exe" /nh /fi "PID eq {pid}" 2>nul | "%SYS%\find.exe" " {pid} " >nul
            if errorlevel 1 goto gone
            if %waited% geq {plan.WaitSeconds} goto force
            "%SYS%\PING.EXE" -n 2 127.0.0.1 >nul
            set /a waited+=1
            goto wait

            :force
            echo %date% %time% Still running after {plan.WaitSeconds}s; forcing it closed.>>"%LOG%"
            "%SYS%\taskkill.exe" /f /pid {pid} >>"%LOG%" 2>&1

            :gone
            echo %date% %time% App has exited.>>"%LOG%"
            if exist "{backup}" del /f /q "{backup}" >nul 2>&1

            rem Move aside rather than delete, so a failed copy can be undone.
            rem Retried because Windows can hold the lock briefly after exit.
            set /a tries=0
            :move
            move /y "{target}" "{backup}" >nul 2>&1
            if not errorlevel 1 goto moved
            set /a tries+=1
            if %tries% geq 10 goto keepold
            "%SYS%\PING.EXE" -n 2 127.0.0.1 >nul
            goto move

            :keepold
            echo %date% %time% FAILED: could not move the current exe aside. Kept the current version.>>"%LOG%"
            {launch}
            exit /b 1

            :moved
            copy /y "{staged}" "{target}" >nul 2>&1
            if not errorlevel 1 goto copied
            echo %date% %time% FAILED: could not copy the new exe. Restoring the current version.>>"%LOG%"
            move /y "{backup}" "{target}" >nul 2>&1
            {launch}
            exit /b 1

            :copied
            echo %date% %time% Updated. Starting the new version.>>"%LOG%"
            del /f /q "{backup}" >nul 2>&1
            {launch}

            rem Clean the staging area last, and never fail the update over it.
            rmdir /s /q "{staging}" >nul 2>&1
            exit /b 0
            """;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort; the staging dir is cleaned later */ }
        catch (UnauthorizedAccessException) { }
    }
}
