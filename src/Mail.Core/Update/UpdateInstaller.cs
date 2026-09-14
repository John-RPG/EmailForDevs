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
        File.WriteAllText(script, SwapScript(
            Environment.ProcessId, staged, currentExePath, stagingDirectory));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = stagingDirectory,
        });
        return staged;
    }

    /// <summary>
    /// The swap, as a batch file. Kept small and readable on purpose: it runs
    /// unsupervised, after the app is gone, with no way to report a failure
    /// except by leaving the old binary working.
    /// </summary>
    static string SwapScript(int pid, string staged, string target, string staging)
    {
        var backup = target + ".old";
        return $"""
            @echo off
            rem Wait for eeeMail (pid {pid}) to exit before touching its exe.
            :wait
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )

            rem Move aside rather than delete, so a failed copy can be undone.
            if exist "{backup}" del /f /q "{backup}"
            move /y "{target}" "{backup}" >nul 2>&1

            copy /y "{staged}" "{target}" >nul 2>&1
            if errorlevel 1 (
                rem Put the working build back and leave everything else alone.
                move /y "{backup}" "{target}" >nul 2>&1
                exit /b 1
            )

            del /f /q "{backup}" >nul 2>&1
            start "" "{target}"

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
