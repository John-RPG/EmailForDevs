using System.Diagnostics;
using Mail.Core.Update;

namespace Mail.Tests;

/// <summary>
/// Runs the generated swap script for real, against stand-in files and a
/// stand-in process, because its failure modes live in cmd.exe - quoting,
/// errorlevels, waiting on a process - where reading the text proves nothing.
/// The relaunch is replaced by a marker file, so nothing is started.
/// </summary>
public sealed class UpdateSwapScriptTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public UpdateSwapScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    sealed record Setup(UpdateInstaller.SwapPlan Plan, string Marker);

    /// <summary>An installed "old" exe, a staged "new" one, and a plan to swap them.</summary>
    Setup Arrange(int pid, string root, bool stageNew = true, int waitSeconds = 30)
    {
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(root, "profile", "update");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);

        var target = Path.Combine(install, "eeeMail.exe");
        File.WriteAllText(target, "old");
        var staged = Path.Combine(staging, "unpacked", "eeeMail.exe");
        if (stageNew)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllText(staged, "new");
        }

        var marker = Path.Combine(root, "launched.txt");
        var plan = new UpdateInstaller.SwapPlan(
            pid, staged, target, staging, UpdateInstaller.LogPathFor(staging), waitSeconds,
            Launch: $"echo launched>>\"{marker.Replace("%", "%%")}\"");
        return new Setup(plan, marker);
    }

    static int Run(UpdateInstaller.SwapPlan plan, string scriptDirectory)
    {
        var script = Path.Combine(scriptDirectory, "apply-update.cmd");
        File.WriteAllText(script, UpdateInstaller.SwapScript(plan));
        using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        Assert.True(cmd.WaitForExit(TimeSpan.FromSeconds(60)), "swap script did not finish");
        return cmd.ExitCode;
    }

    /// <summary>A pid that belonged to a process which has already exited.</summary>
    static int ExitedPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void Swaps_in_the_new_exe_relaunches_and_cleans_up()
    {
        var s = Arrange(ExitedPid(), _dir);

        var exit = Run(s.Plan, _dir);

        Assert.Equal(0, exit);
        Assert.Equal("new", File.ReadAllText(s.Plan.Target));
        Assert.False(File.Exists(s.Plan.Target + ".old"));
        Assert.True(File.Exists(s.Marker), "the app was not relaunched");
        Assert.False(Directory.Exists(s.Plan.Staging));
        Assert.Contains("Updated.", File.ReadAllText(s.Plan.LogPath));
    }

    [Fact]
    public void Restores_and_relaunches_the_old_exe_when_the_copy_fails()
    {
        // The case that used to leave the user with nothing running at all.
        var s = Arrange(ExitedPid(), _dir, stageNew: false);

        var exit = Run(s.Plan, _dir);

        Assert.Equal(1, exit);
        Assert.Equal("old", File.ReadAllText(s.Plan.Target));
        Assert.True(File.Exists(s.Marker), "the old version was not relaunched");
        Assert.Contains("FAILED", File.ReadAllText(s.Plan.LogPath));
    }

    [Fact]
    public void Forces_closed_an_app_that_never_exits_then_swaps()
    {
        // Stands in for an app hung on shutdown: a process that would otherwise
        // outlive the test by minutes.
        using var hung = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            var s = Arrange(hung.Id, _dir, waitSeconds: 1);

            var exit = Run(s.Plan, _dir);

            Assert.Equal(0, exit);
            Assert.True(hung.WaitForExit(TimeSpan.FromSeconds(5)), "the hung process was not closed");
            Assert.Equal("new", File.ReadAllText(s.Plan.Target));
            Assert.Contains("forcing it closed", File.ReadAllText(s.Plan.LogPath));
        }
        finally
        {
            if (!hung.HasExited) hung.Kill();
        }
    }

    [Fact]
    public void Survives_a_percent_sign_in_the_install_path()
    {
        // Batch would read "%x%" as a variable and swap a different path.
        var root = Path.Combine(_dir, "100%x% done");
        var s = Arrange(ExitedPid(), root);

        var exit = Run(s.Plan, _dir);

        Assert.Equal(0, exit);
        Assert.Equal("new", File.ReadAllText(s.Plan.Target));
        Assert.True(File.Exists(s.Marker));
    }
}
