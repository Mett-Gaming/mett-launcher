using MettLauncher.Core.IO;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class LibraryStateTests
{
    [Fact]
    public void Roundtrips_through_library_json()
    {
        using var tmp = new TempDir();
        var played = new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
        var store = new LibraryStore(tmp.Paths);
        store.UpdateGame("kasse0", s =>
        {
            s.InstalledVersion = "0.17.0";
            s.InstalledTag = "v0.17.0";
            s.InstallPath = @"C:\Games\kasse0";
            s.PinnedTag = "v0.17.0";
            s.TotalPlaySeconds = 4242;
            s.LastPlayed = played;
        });

        var reloaded = new LibraryStore(tmp.Paths).GetGame("KASSE0"); // IDs case-insensitiv
        Assert.NotNull(reloaded);
        Assert.Equal("0.17.0", reloaded.InstalledVersion);
        Assert.Equal(@"C:\Games\kasse0", reloaded.InstallPath);
        Assert.Equal("v0.17.0", reloaded.PinnedTag);
        Assert.Equal(TimeSpan.FromSeconds(4242), reloaded.TotalPlaytime);
        Assert.Equal(played, reloaded.LastPlayed);
        Assert.True(reloaded.IsInstalled);

        var json = File.ReadAllText(tmp.Paths.LibraryFile);
        Assert.Contains("\"installedVersion\": \"0.17.0\"", json);
        Assert.DoesNotContain("isInstalled", json);
    }

    [Fact]
    public void Writes_atomically_without_leftover_temp_files()
    {
        using var tmp = new TempDir();
        var store = new LibraryStore(tmp.Paths);
        for (var i = 0; i < 20; i++) store.UpdateGame("g", s => s.TotalPlaySeconds = i);
        var files = Directory.GetFiles(tmp.Paths.Root);
        Assert.Equal([tmp.Paths.LibraryFile], files);
    }

    [Fact]
    public void Missing_file_gives_empty_state()
    {
        using var tmp = new TempDir();
        Assert.Empty(new LibraryStore(tmp.Paths).Snapshot().Games);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Root);
        File.WriteAllText(tmp.Paths.LibraryFile, "{ kaputt");

        var store = new LibraryStore(tmp.Paths);

        Assert.Empty(store.Snapshot().Games);
        Assert.Single(Directory.GetFiles(tmp.Paths.Root, "library.json.corrupt-*"));
    }

    [Fact]
    public void Failed_mutation_does_not_change_state()
    {
        using var tmp = new TempDir();
        var store = new LibraryStore(tmp.Paths);
        store.UpdateGame("g", s => s.TotalPlaySeconds = 1);
        Assert.Throws<InvalidOperationException>(() => store.UpdateGame("g", s =>
        {
            s.TotalPlaySeconds = 99;
            throw new InvalidOperationException();
        }));
        Assert.Equal(1, store.GetGame("g")!.TotalPlaySeconds);
    }

    [Fact]
    public void Snapshots_are_independent_copies_and_changed_event_fires()
    {
        using var tmp = new TempDir();
        var store = new LibraryStore(tmp.Paths);
        LibraryState? seen = null;
        store.Changed += (_, s) => seen = s;
        store.UpdateGame("g", s => s.TotalPlaySeconds = 5);

        var snap = store.Snapshot();
        snap.Games["g"].TotalPlaySeconds = 1000;
        Assert.Equal(5, store.GetGame("g")!.TotalPlaySeconds);
        Assert.Equal(5, seen!.Games["g"].TotalPlaySeconds);
    }

    [Fact]
    public void AtomicFile_overwrites_existing_file()
    {
        using var tmp = new TempDir();
        var path = tmp.Sub("a", "b.json");
        AtomicFile.WriteAllText(path, "eins");
        AtomicFile.WriteAllText(path, "zwei");
        Assert.Equal("zwei", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(tmp.Sub("a")));
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_and_default_library_dir()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Paths);
        Assert.True(store.Current.AutoUpdate);
        Assert.False(store.Current.IncludePrereleases);
        Assert.Equal(tmp.Paths.DefaultGamesDir, store.LibraryDir);
        Assert.EndsWith(Path.Combine("MettLauncher", "games"), LauncherPaths.Default().DefaultGamesDir);
    }

    [Fact]
    public void Saves_and_reloads()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Paths);
        var lib = tmp.Sub("MeineSpiele");
        store.Update(s => s with { LibraryDir = lib, AutoUpdate = false, IncludePrereleases = true });

        var reloaded = new SettingsStore(tmp.Paths);
        Assert.False(reloaded.Current.AutoUpdate);
        Assert.True(reloaded.Current.IncludePrereleases);
        Assert.Equal(lib, reloaded.LibraryDir);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Paths.Root);
        File.WriteAllText(tmp.Paths.SettingsFile, "nope");
        Assert.True(new SettingsStore(tmp.Paths).Current.AutoUpdate);
    }
}
