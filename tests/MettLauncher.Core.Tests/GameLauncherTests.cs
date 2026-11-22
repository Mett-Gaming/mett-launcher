using System.Diagnostics;
using MettLauncher.Core.Launch;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class GameLauncherTests
{
    private sealed class FakeProcess : IRunningProcess
    {
        public readonly TaskCompletionSource Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 4711;
        public int ExitCode { get; set; }
        public Task WaitForExitAsync(CancellationToken ct = default) => Exit.Task.WaitAsync(ct);
        public void Dispose() { }
    }

    private sealed class FakeStarter : IProcessStarter
    {
        public readonly List<ProcessStartInfo> Started = [];
        public FakeProcess? Last;
        public bool ExternallyRunning;

        public IRunningProcess Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            return Last = new FakeProcess();
        }

        public bool IsRunning(string exePath) => ExternallyRunning;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly LibraryStore Library;
        public readonly FakeStarter Starter = new();
        public readonly ManualTime Time = new(new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero));
        public readonly GameLauncher Launcher;

        public Fixture(bool installed = true)
        {
            Library = new LibraryStore(Tmp.Paths);
            Launcher = new GameLauncher(Library, Starter, Time);
            if (installed)
            {
                Directory.CreateDirectory(GameDir);
                File.WriteAllText(Path.Combine(GameDir, "KASSE0.exe"), "");
                Library.UpdateGame("kasse0", s => { s.InstallPath = GameDir; s.InstalledVersion = "0.17.0"; });
            }
        }

        public string GameDir => Tmp.Sub("games", "kasse0");
        public void Dispose() => Tmp.Dispose();
    }

    [Fact]
    public async Task Starts_in_install_dir_and_accumulates_playtime()
    {
        using var f = new Fixture();
        var game = TestData.Kasse0(b => b.Args = ["--windowed", "mit leerzeichen"]);

        var session = f.Launcher.Launch(game);
        var psi = Assert.Single(f.Starter.Started);
        Assert.Equal(Path.Combine(f.GameDir, "KASSE0.exe"), psi.FileName);
        Assert.Equal(f.GameDir, psi.WorkingDirectory);
        Assert.Equal(["--windowed", "mit leerzeichen"], psi.ArgumentList);
        Assert.True(f.Launcher.IsRunning("kasse0"));
        Assert.Equal(f.Time.Now, f.Library.GetGame("kasse0")!.LastPlayed);

        f.Time.Advance(TimeSpan.FromMinutes(42));
        f.Starter.Last!.ExitCode = 0;
        f.Starter.Last.Exit.SetResult();
        var exit = await session.Exited.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromMinutes(42), exit.Duration);
        Assert.Equal(0, exit.ExitCode);
        Assert.False(f.Launcher.IsRunning("kasse0"));
        var state = f.Library.GetGame("kasse0")!;
        Assert.Equal(42 * 60, state.TotalPlaySeconds);
        Assert.Equal(f.Time.Now, state.LastPlayed);

        // zweite Sitzung addiert
        var second = f.Launcher.Launch(game);
        f.Time.Advance(TimeSpan.FromMinutes(8));
        f.Starter.Last!.Exit.SetResult();
        await second.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(50 * 60, f.Library.GetGame("kasse0")!.TotalPlaySeconds);
    }

    [Fact]
    public async Task Prevents_double_start()
    {
        using var f = new Fixture();
        var session = f.Launcher.Launch(TestData.Kasse0());
        Assert.Throws<GameAlreadyRunningException>(() => f.Launcher.Launch(TestData.Kasse0()));
        Assert.Single(f.Starter.Started);

        f.Starter.Last!.Exit.SetResult();
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        f.Launcher.Launch(TestData.Kasse0()); // danach wieder möglich
    }

    [Fact]
    public void Prevents_start_when_already_running_outside_launcher()
    {
        using var f = new Fixture();
        f.Starter.ExternallyRunning = true;
        Assert.Throws<GameAlreadyRunningException>(() => f.Launcher.Launch(TestData.Kasse0()));
        Assert.False(f.Launcher.IsRunning("kasse0"));
    }

    [Fact]
    public void Not_installed_throws()
    {
        using var f = new Fixture(installed: false);
        Assert.Throws<GameNotInstalledException>(() => f.Launcher.Launch(TestData.Kasse0()));
    }

    [Fact]
    public void Missing_exe_throws()
    {
        using var f = new Fixture();
        File.Delete(Path.Combine(f.GameDir, "KASSE0.exe"));
        Assert.Throws<GameNotInstalledException>(() => f.Launcher.Launch(TestData.Kasse0()));
    }

    [Fact]
    public async Task Events_fire()
    {
        using var f = new Fixture();
        GameSession? started = null;
        GameExitInfo? exited = null;
        f.Launcher.GameStarted += (_, s) => started = s;
        f.Launcher.GameExited += (_, e) => exited = e;

        var session = f.Launcher.Launch(TestData.Kasse0());
        Assert.Same(session, started);
        f.Starter.Last!.Exit.SetResult();
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("kasse0", exited!.GameId);
    }

    [Fact]
    public async Task Real_process_runs_and_is_measured()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var dir = tmp.Sub("cmdgame");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(dir, "game.exe"));
        var library = new LibraryStore(tmp.Paths);
        library.UpdateGame("cmd", s => { s.InstallPath = dir; s.InstalledVersion = "1.0.0"; });
        var launcher = new GameLauncher(library);
        var game = new Catalog.GameEntry
        {
            Id = "cmd", Name = "cmd", Repo = "a/b", AssetPattern = ".*", Exe = "game.exe",
            Args = ["/c", "exit 3"],
        };

        var session = launcher.Launch(game);
        var exit = await session.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, exit.ExitCode);
        Assert.NotNull(library.GetGame("cmd")!.LastPlayed);
    }
}
