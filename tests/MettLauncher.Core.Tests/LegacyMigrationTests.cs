using System.Text.Json.Nodes;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class LegacyMigrationTests
{
    [Fact]
    public void Moves_launcher_data_rewrites_paths_and_leaves_velopack_files()
    {
        using var tmp = new TempDir();
        var legacy = tmp.Sub("MettLauncher");
        var target = new LauncherPaths(tmp.Sub("MettGaming", "Launcher"));

        // Alter Zustand: Velopack-Installation + Launcher-Daten im selben Ordner.
        Directory.CreateDirectory(Path.Combine(legacy, "current"));
        File.WriteAllText(Path.Combine(legacy, "current", "MettLauncher.exe"), "exe");
        File.WriteAllText(Path.Combine(legacy, "Update.exe"), "upd");
        Directory.CreateDirectory(Path.Combine(legacy, "games", "kasse0"));
        File.WriteAllText(Path.Combine(legacy, "games", "kasse0", "KASSE0.exe"), "game");
        Directory.CreateDirectory(Path.Combine(legacy, "net"));
        File.WriteAllText(Path.Combine(legacy, "net", "secret.key"), "key");
        var oldGame = Path.Combine(legacy, "games", "kasse0");
        File.WriteAllText(Path.Combine(legacy, "library.json"), new JsonObject
        {
            ["version"] = 1,
            ["games"] = new JsonObject { ["kasse0"] = new JsonObject { ["installPath"] = oldGame, ["installedVersion"] = "0.19.2" } },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(legacy, "settings.json"), new JsonObject
        {
            ["libraryDir"] = Path.Combine(legacy, "games"),
            ["displayName"] = "Bob",
        }.ToJsonString());

        var result = LegacyDataMigration.Run(target, legacy);

        Assert.True(result.Migrated);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Path.Combine(target.DefaultGamesDir, "kasse0", "KASSE0.exe")));
        Assert.True(File.Exists(Path.Combine(target.NetDir, "secret.key")));
        Assert.False(Directory.Exists(Path.Combine(legacy, "games")));
        // Velopack bleibt unangetastet
        Assert.True(File.Exists(Path.Combine(legacy, "Update.exe")));
        Assert.True(File.Exists(Path.Combine(legacy, "current", "MettLauncher.exe")));

        var game = new LibraryStore(target).GetGame("kasse0")!;
        Assert.Equal(Path.Combine(target.DefaultGamesDir, "kasse0"), game.InstallPath);
        Assert.Equal("0.19.2", game.InstalledVersion);
        var settings = JsonNode.Parse(File.ReadAllText(target.SettingsFile))!;
        Assert.Equal(target.DefaultGamesDir, (string)settings["libraryDir"]!);
        Assert.Equal("Bob", (string)settings["displayName"]!);
    }

    [Fact]
    public void Removes_empty_legacy_folder_and_does_nothing_twice()
    {
        using var tmp = new TempDir();
        var legacy = tmp.Sub("MettLauncher");
        var target = new LauncherPaths(tmp.Sub("MettGaming", "Launcher"));
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        Assert.True(LegacyDataMigration.Run(target, legacy).Migrated);
        Assert.False(Directory.Exists(legacy)); // leer -> entfernt, Setup fragt nicht mehr nach

        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"displayName\":\"neu\"}");
        Assert.False(LegacyDataMigration.Run(target, legacy).Migrated); // neuer Ordner schon in Benutzung
    }

    [Fact]
    public void Ignores_missing_or_unrelated_legacy_folder()
    {
        using var tmp = new TempDir();
        var target = new LauncherPaths(tmp.Sub("new"));
        Assert.False(LegacyDataMigration.Run(target, tmp.Sub("gibt-es-nicht")).Migrated);

        var onlyVelopack = tmp.Sub("MettLauncher");
        Directory.CreateDirectory(Path.Combine(onlyVelopack, "current"));
        Assert.False(LegacyDataMigration.Run(target, onlyVelopack).Migrated);
        Assert.True(Directory.Exists(Path.Combine(onlyVelopack, "current")));
    }
}
