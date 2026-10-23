using System.Text;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Platform;
using FPSHubRTX.Core.Scanning;
using FPSHubRTX.Core.Scanning.Scanners;

namespace FPSHubRTX.Core.Tests;

public class VdfParserTests
{
    [Fact]
    public void Parses_nested_objects_escapes_and_comments()
    {
        var text = "// comment\n\"root\"\n{\n  \"path\" \"C:\\\\Games\\\\Steam\"\n  \"quote\" \"say \\\"hi\\\"\"\n  \"child\" { \"a\" \"1\" }\n  unquoted value\n}";
        var root = VdfParser.Parse(text);
        Assert.Equal("C:\\Games\\Steam", root.Path("root", "path")!.Value);
        Assert.Equal("say \"hi\"", root.Path("root", "quote")!.Value);
        Assert.Equal("1", root.Path("ROOT", "CHILD", "a")!.Value); // case-insensitive
        Assert.Equal("value", root.Path("root", "unquoted")!.Value);
    }

    [Fact]
    public void Malformed_input_does_not_throw()
    {
        var root = VdfParser.Parse("\"a\" { \"b\" { \"c\" ");
        Assert.NotNull(root);
        Assert.NotNull(VdfParser.Parse("}}}}"));
        Assert.NotNull(VdfParser.Parse(""));
    }
}

public class SteamScannerTests
{
    [Fact]
    public void Finds_games_in_all_libraries_and_skips_tools()
    {
        using var t = new TempDir();
        var steam = t.Dir("Steam");
        var lib2 = t.Dir("D", "SteamLibrary");
        t.File("Steam/steamapps/libraryfolders.vdf", VdfText.LibraryFolders(steam, lib2, Path.Combine(t.Root, "Z", "Disconnected")));
        t.File("Steam/steamapps/appmanifest_730.acf", VdfText.AppManifest("730", "Counter-Strike 2", "Counter-Strike Global Offensive"));
        t.Exe("Steam/steamapps/common/Counter-Strike Global Offensive/game/bin/win64/cs2.exe");
        t.File("Steam/steamapps/appmanifest_228980.acf", VdfText.AppManifest("228980", "Steamworks Common Redistributables", "Steamworks Shared"));
        t.File("D/SteamLibrary/steamapps/appmanifest_1091500.acf", VdfText.AppManifest("1091500", "Cyberpunk 2077", "Cyberpunk 2077"));
        t.Exe("D/SteamLibrary/steamapps/common/Cyberpunk 2077/bin/x64/Cyberpunk2077.exe", 900_000);
        t.Exe("D/SteamLibrary/steamapps/common/Cyberpunk 2077/bin/x64/REDprelauncher.exe", 100_000);
        t.File("D/SteamLibrary/steamapps/appmanifest_42.acf", VdfText.AppManifest("42", "Downloading Game", "Downloading Game", stateFlags: 1026));
        t.Dir("D", "SteamLibrary", "steamapps", "common", "Downloading Game");
        t.File("Steam/appcache/librarycache/730_library_600x900.jpg", "jpg");

        var reg = new InMemoryRegistry().Set(RegHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", steam);
        var result = new SteamScanner().Scan(Ctx.Make(t, reg));

        Assert.True(result.LauncherDetected);
        Assert.True(result.Completed);
        Assert.Equal(3, result.Games.Count);
        Assert.DoesNotContain(result.Games, g => g.SourceId == "228980");
        Assert.Contains(result.Warnings, w => w.Contains("Disconnected"));

        var cs = result.Games.Single(g => g.SourceId == "730");
        Assert.Equal("steam:730", cs.Id);
        Assert.Equal(InstallStatus.Installed, cs.Status);
        Assert.Equal("steam://rungameid/730", cs.LaunchMethods[0].Target);
        Assert.Equal(LaunchMethodKind.Uri, cs.LaunchMethods[0].Kind);
        Assert.EndsWith("cs2.exe", cs.ExecutablePath);
        Assert.EndsWith("730_library_600x900.jpg", cs.CoverImagePath);
        Assert.Equal("steam://validate/730", cs.VerifyFilesUri);

        var cp = result.Games.Single(g => g.SourceId == "1091500");
        Assert.EndsWith("Cyberpunk2077.exe", cp.ExecutablePath);
        Assert.Equal(InstallStatus.Incomplete, result.Games.Single(g => g.SourceId == "42").Status);
    }

    [Fact]
    public void No_steam_installed_reports_not_detected()
    {
        using var t = new TempDir();
        var result = new SteamScanner().Scan(Ctx.Make(t));
        Assert.False(result.LauncherDetected);
        Assert.True(result.Completed);
        Assert.Empty(result.Games);
    }

    [Fact]
    public void Old_libraryfolders_format_is_supported()
    {
        using var t = new TempDir();
        var steam = t.Dir("Steam");
        var lib2 = t.Dir("E", "Lib");
        t.File("Steam/steamapps/libraryfolders.vdf", $"\"LibraryFolders\"\n{{\n\"TimeNextStatsReport\" \"123\"\n\"1\" \"{VdfText.Escape(lib2)}\"\n}}");
        t.File("E/Lib/steamapps/appmanifest_10.acf", VdfText.AppManifest("10", "Counter-Strike", "Half-Life"));
        t.Dir("E", "Lib", "steamapps", "common", "Half-Life");
        var reg = new InMemoryRegistry().Set(RegHive.LocalMachine32, @"SOFTWARE\Valve\Steam", "InstallPath", steam);
        var result = new SteamScanner().Scan(Ctx.Make(t, reg));
        Assert.Single(result.Games);
    }

    [Fact]
    public void Install_dir_path_traversal_is_rejected()
    {
        using var t = new TempDir();
        var steam = t.Dir("Steam");
        t.File("Steam/steamapps/appmanifest_99.acf", VdfText.AppManifest("99", "Evil", "../../../../Windows"));
        var reg = new InMemoryRegistry().Set(RegHive.CurrentUser, @"Software\Valve\Steam", "SteamPath", steam);
        var game = new SteamScanner().Scan(Ctx.Make(t, reg)).Games.Single();
        Assert.Null(game.InstallPath);
    }
}

public class EpicScannerTests
{
    private static string Item(string app, string name, string location, string exe = "Game.exe", bool incomplete = false, string? mainGame = null, string cats = "\"public\",\"games\",\"applications\"")
        => $$"""
           {
             "FormatVersion": 0,
             "bIsIncompleteInstall": {{(incomplete ? "true" : "false")}},
             "LaunchExecutable": "{{exe}}",
             "LaunchCommand": "",
             "DisplayName": "{{name}}",
             "InstallLocation": "{{location.Replace("\\", "\\\\")}}",
             "CatalogNamespace": "ns{{app}}",
             "CatalogItemId": "item{{app}}",
             "AppName": "{{app}}",
             "MainGameAppName": "{{mainGame ?? app}}",
             "AppCategories": [{{cats}}]
           }
           """;

    [Fact]
    public void Reads_manifests_and_skips_dlc_and_broken_files()
    {
        using var t = new TempDir();
        var env = Ctx.Env(t);
        var manifests = Path.Combine(env.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        var gameDir = t.Dir("EpicGames", "Fortnite");
        t.Exe("EpicGames/Fortnite/FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "a.item"), Item("Fortnite", "Fortnite", gameDir, "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe"));
        File.WriteAllText(Path.Combine(manifests, "b.item"), Item("DlcPack", "Some DLC", gameDir, mainGame: "Fortnite"));
        File.WriteAllText(Path.Combine(manifests, "c.item"), Item("Addon", "Addon", gameDir, cats: "\"addons\""));
        File.WriteAllText(Path.Combine(manifests, "d.item"), "{ this is not json");
        var dl = t.Dir("EpicGames", "Downloading");
        File.WriteAllText(Path.Combine(manifests, "e.item"), Item("Dl", "Downloading", dl, incomplete: true));

        var ctx = new ScanContext { Environment = env, Registry = new InMemoryRegistry(), Drives = new FixedDriveProvider(), Settings = new ScanSettings() };
        var result = new EpicScanner().Scan(ctx);

        Assert.True(result.LauncherDetected);
        Assert.Equal(2, result.Games.Count);
        var fn = result.Games.Single(g => g.SourceId == "Fortnite");
        Assert.StartsWith("com.epicgames.launcher://apps/nsFortnite%3AitemFortnite%3AFortnite?action=launch", fn.LaunchMethods[0].Target);
        Assert.EndsWith("FortniteClient-Win64-Shipping.exe", fn.ExecutablePath);
        Assert.Equal(InstallStatus.Incomplete, result.Games.Single(g => g.SourceId == "Dl").Status);
        Assert.Contains(result.Warnings, w => w.Contains("d.item"));
    }
}

public class GogScannerTests
{
    [Fact]
    public void Reads_registry_and_play_tasks()
    {
        using var t = new TempDir();
        var dir = t.Dir("GOG Games", "Witcher 3");
        t.Exe("GOG Games/Witcher 3/bin/x64/witcher3.exe");
        t.File("GOG Games/Witcher 3/goggame-1207664663.info",
            "{\"playTasks\":[{\"isPrimary\":true,\"type\":\"FileTask\",\"path\":\"bin\\\\x64\\\\witcher3.exe\",\"arguments\":\"-dx12\"}]}");
        var reg = new InMemoryRegistry()
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\1207664663", "gameName", "The Witcher 3")
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\1207664663", "path", dir)
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\1207664663", "gameID", "1207664663")
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\999", "gameName", "A DLC")
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\999", "path", dir)
            .Set(RegHive.LocalMachine32, @"SOFTWARE\GOG.com\Games\999", "dependsOn", "1207664663");

        var result = new GogScanner().Scan(Ctx.Make(t, reg));
        var g = Assert.Single(result.Games);
        Assert.Equal("gog:1207664663", g.Id);
        Assert.EndsWith("witcher3.exe", g.ExecutablePath);
        Assert.Equal(LaunchMethodKind.Executable, g.LaunchMethods[0].Kind);
        Assert.Equal("-dx12", g.LaunchMethods[0].Arguments);
    }
}

public class OtherLauncherScannerTests
{
    [Fact]
    public void Ubisoft_installs_use_uplay_protocol()
    {
        using var t = new TempDir();
        var dir = t.Dir("Ubisoft", "Far Cry 6");
        var reg = new InMemoryRegistry()
            .Set(RegHive.LocalMachine32, @"SOFTWARE\Ubisoft\Launcher\Installs\5266", "InstallDir", dir + "/")
            .Set(RegHive.LocalMachine32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install 5266", "DisplayName", "Far Cry® 6");
        var g = Assert.Single(new UbisoftScanner().Scan(Ctx.Make(t, reg)).Games);
        Assert.Equal("Far Cry 6", g.Name); // ® stripped
        Assert.Equal("uplay://launch/5266/0", g.LaunchMethods[0].Target);
        Assert.Equal(InstallStatus.Installed, g.Status);
    }

    [Fact]
    public void Riot_products_launch_through_riot_client()
    {
        using var t = new TempDir();
        var env = Ctx.Env(t);
        var client = t.Exe("Riot Games/Riot Client/RiotClientServices.exe");
        var valorant = t.Dir("Riot Games", "VALORANT");
        File.WriteAllText(Path.Combine(env.ProgramData, "Riot Games", "RiotClientInstalls.json").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
            $"{{\"rc_default\": \"{client.Replace("\\", "\\\\")}\"}}");
        var meta = Path.Combine(env.ProgramData, "Riot Games", "Metadata", "valorant.live");
        Directory.CreateDirectory(meta);
        File.WriteAllText(Path.Combine(meta, "valorant.live.product_settings.yaml"), $"product_install_full_path: \"{valorant}\"\nproduct_install_root: \"x\"\n  nested: ignored\n");
        Directory.CreateDirectory(Path.Combine(env.ProgramData, "Riot Games", "Metadata", "riot_client"));

        var ctx = new ScanContext { Environment = env, Registry = new InMemoryRegistry(), Drives = new FixedDriveProvider(), Settings = new ScanSettings() };
        var g = Assert.Single(new RiotScanner().Scan(ctx).Games);
        Assert.Equal("VALORANT", g.Name);
        Assert.Equal(LaunchMethodKind.LauncherCommand, g.LaunchMethods[0].Kind);
        Assert.Equal("--launch-product=valorant --launch-patchline=live", g.LaunchMethods[0].Arguments);
    }

    [Fact]
    public void Ea_games_read_installerdata_and_skip_the_ea_app()
    {
        using var t = new TempDir();
        var dir = t.Dir("EA Games", "Battlefield 2042");
        t.Exe("EA Games/Battlefield 2042/BF2042.exe");
        t.File("EA Games/Battlefield 2042/__Installer/installerdata.xml",
            "<?xml version=\"1.0\"?><DiPManifest><gameTitles><gameTitle locale=\"en_US\">Battlefield™ 2042</gameTitle></gameTitles>" +
            "<contentIDs><contentID>Origin.OFR.50.0004683</contentID></contentIDs>" +
            "<runtime><launcher><filePath>[HKEY_LOCAL_MACHINE\\SOFTWARE\\EA Games\\Battlefield 2042\\Install Dir]BF2042.exe</filePath></launcher></runtime></DiPManifest>");
        var u = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var reg = new InMemoryRegistry()
            .Set(RegHive.LocalMachine32, u + @"\{BF2042}", "DisplayName", "Battlefield 2042")
            .Set(RegHive.LocalMachine32, u + @"\{BF2042}", "Publisher", "Electronic Arts")
            .Set(RegHive.LocalMachine32, u + @"\{BF2042}", "InstallLocation", dir)
            .Set(RegHive.LocalMachine, u + @"\EAapp", "DisplayName", "EA app")
            .Set(RegHive.LocalMachine, u + @"\EAapp", "Publisher", "Electronic Arts")
            .Set(RegHive.LocalMachine, u + @"\EAapp", "InstallLocation", t.Dir("EA app"));
        var g = Assert.Single(new EaScanner().Scan(Ctx.Make(t, reg)).Games);
        Assert.Equal("Battlefield 2042", g.Name);
        Assert.Equal("ea:Origin.OFR.50.0004683", g.Id);
        Assert.EndsWith("BF2042.exe", g.ExecutablePath);
    }

    [Fact]
    public void Ea_installerdata_with_dtd_is_rejected_safely()
    {
        using var t = new TempDir();
        var file = t.File("x/__Installer/installerdata.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><DiPManifest>&e;</DiPManifest>");
        Assert.Null(EaScanner.ReadInstallerData(file, t.Root));
    }

    [Fact]
    public void BattleNet_known_titles_get_product_codes()
    {
        using var t = new TempDir();
        var env = Ctx.Env(t);
        var bnet = Path.Combine(env.ProgramFilesX86, "Battle.net", "Battle.net.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bnet)!);
        File.WriteAllBytes(bnet, new byte[10]);
        var dir = t.Dir("Overwatch");
        var u = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var reg = new InMemoryRegistry()
            .Set(RegHive.LocalMachine32, u + @"\Overwatch", "DisplayName", "Overwatch")
            .Set(RegHive.LocalMachine32, u + @"\Overwatch", "Publisher", "Blizzard Entertainment")
            .Set(RegHive.LocalMachine32, u + @"\Overwatch", "InstallLocation", dir);
        var ctx = new ScanContext { Environment = env, Registry = reg, Drives = new FixedDriveProvider(), Settings = new ScanSettings() };
        var g = Assert.Single(new BattleNetScanner().Scan(ctx).Games);
        Assert.Equal("--exec=\"launch Pro\"", g.LaunchMethods[0].Arguments);
        Assert.Equal("Fen", BattleNetScanner.ProductCode("Diablo IV"));
        Assert.Null(BattleNetScanner.ProductCode("Unknown Blizzard Game"));
    }

    [Fact]
    public void Xbox_games_found_via_gamingroot_and_package_catalog()
    {
        using var t = new TempDir();
        var drive = t.Dir("X");
        var header = Encoding.ASCII.GetBytes("RGBX").Concat(BitConverter.GetBytes(1)).Concat(Encoding.Unicode.GetBytes("XboxGames\0")).ToArray();
        t.Bytes("X/.GamingRoot", header);
        t.File("X/XboxGames/Forza Horizon 5/Content/MicrosoftGame.config",
            "<Game configVersion=\"1\"><Identity Name=\"Microsoft.SunriseBaseGame\" Publisher=\"CN=x\" Version=\"1.0\"/>" +
            "<ExecutableList><Executable Name=\"ForzaHorizon5.exe\" Id=\"Game\"/></ExecutableList>" +
            "<ShellVisuals DefaultDisplayName=\"Forza Horizon 5\" Square480x480Logo=\"GraphicsLogo.png\"/></Game>");
        t.Exe("X/XboxGames/Forza Horizon 5/Content/ForzaHorizon5.exe");
        t.File("X/XboxGames/Forza Horizon 5/Content/GraphicsLogo.png", "png");
        var catalog = new FixedPackageCatalog(new InstalledPackage("Microsoft.SunriseBaseGame", "Microsoft.SunriseBaseGame_8wekyb3d8bbwe", null, "Forza", new[] { "Game" }));
        var drives = new FixedDriveProvider(new DriveDescriptor(drive, DriveKind.Fixed, true));

        Assert.Equal(new[] { "XboxGames" }, XboxScanner.ReadGamingRootFile(Path.Combine(drive, ".GamingRoot")));
        var g = Assert.Single(new XboxScanner().Scan(Ctx.Make(t, packages: catalog, drives: drives)).Games);
        Assert.Equal("Forza Horizon 5", g.Name);
        Assert.Equal(LaunchMethodKind.PackagedApp, g.LaunchMethods[0].Kind);
        Assert.Equal("Microsoft.SunriseBaseGame_8wekyb3d8bbwe!Game", g.LaunchMethods[0].Target);
        Assert.EndsWith("GraphicsLogo.png", g.CoverImagePath);
    }

    [Fact]
    public void Minecraft_and_roblox_are_detected()
    {
        using var t = new TempDir();
        var env = Ctx.Env(t);
        var v1 = Path.Combine(env.LocalAppData, "Roblox", "Versions", "version-abc");
        Directory.CreateDirectory(v1);
        File.WriteAllBytes(Path.Combine(v1, "RobloxPlayerBeta.exe"), new byte[100]);
        var legacy = Path.Combine(env.ProgramFilesX86, "Minecraft Launcher");
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "MinecraftLauncher.exe"), new byte[100]);
        var catalog = new FixedPackageCatalog(new InstalledPackage("Microsoft.MinecraftUWP", "Microsoft.MinecraftUWP_8wekyb3d8bbwe", null, "Minecraft", new[] { "App" }));
        var ctx = new ScanContext { Environment = env, Registry = new InMemoryRegistry(), Drives = new FixedDriveProvider(), Settings = new ScanSettings(), Packages = catalog };

        var mc = new MinecraftScanner().Scan(ctx);
        Assert.Equal(2, mc.Games.Count);
        Assert.Contains(mc.Games, g => g.LaunchMethods[0].Target == "Microsoft.MinecraftUWP_8wekyb3d8bbwe!App");
        var rb = Assert.Single(new RobloxScanner().Scan(ctx).Games);
        Assert.EndsWith("RobloxPlayerBeta.exe", rb.ExecutablePath);
    }
}

public class FolderScannerTests
{
    [Fact]
    public void Detects_games_in_user_folders_and_containers_but_not_steam_libraries()
    {
        using var t = new TempDir();
        var games = t.Dir("MyGames");
        t.Exe("MyGames/Hollow Knight/hollow_knight.exe");
        t.Dir("MyGames", "Hollow Knight", "hollow_knight_Data");
        t.Exe("MyGames/Hollow Knight/UnityCrashHandler64.exe");
        t.Dir("MyGames", "NoExe");
        t.File("MyGames/NoExe/data.bin", "x");
        // container with two games
        t.Exe("MyGames/GOG Games/Game A/GameA.exe");
        t.File("MyGames/GOG Games/Game A/steam_api64.dll", "");
        t.Exe("MyGames/GOG Games/Game B/GameB.exe");
        t.File("MyGames/GOG Games/Game B/UnityPlayer.dll", "");
        // steam library inside the folder must be skipped
        t.Exe("MyGames/SteamLibrary/steamapps/common/X/x.exe");
        // excluded folder
        t.Exe("MyGames/Excluded/thing.exe");

        var settings = new ScanSettings { CustomFolders = { games }, ExcludedDirectories = { Path.Combine(games, "Excluded") }, ScanCommonGameFolders = false };
        var result = new FolderScanner().Scan(Ctx.Make(t, settings: settings));
        var names = result.Games.Select(g => g.Name).OrderBy(n => n).ToList();

        Assert.Contains("Hollow Knight", names);
        Assert.Contains("Game A", names);
        Assert.Contains("Game B", names);
        Assert.DoesNotContain("SteamLibrary", names);
        Assert.DoesNotContain("Excluded", names);
        Assert.DoesNotContain("NoExe", names);
        Assert.DoesNotContain("GOG Games", names);
        Assert.EndsWith("hollow_knight.exe", result.Games.Single(g => g.Name == "Hollow Knight").ExecutablePath);
    }

    [Fact]
    public void Missing_custom_folder_is_reported_not_thrown()
    {
        using var t = new TempDir();
        var settings = new ScanSettings { CustomFolders = { Path.Combine(t.Root, "gone") }, ScanCommonGameFolders = false };
        var result = new FolderScanner().Scan(Ctx.Make(t, settings: settings));
        Assert.True(result.Completed);
        Assert.Contains(result.Warnings, w => w.Contains("not available"));
    }

    [Fact]
    public void Common_games_folder_on_each_ready_drive_only()
    {
        using var t = new TempDir();
        var d1 = t.Dir("D1");
        var usb = t.Dir("USB");
        t.Exe("D1/Games/Celeste/Celeste.exe");
        t.Exe("USB/Games/Portable/Portable.exe");
        var drives = new FixedDriveProvider(
            new DriveDescriptor(d1, DriveKind.Fixed, true),
            new DriveDescriptor(usb, DriveKind.Removable, true),
            new DriveDescriptor(Path.Combine(t.Root, "NotReady"), DriveKind.Fixed, false));

        var withoutRemovable = new FolderScanner().Scan(Ctx.Make(t, settings: new ScanSettings { ScanCommonGameFolders = true }, drives: drives));
        Assert.Equal(new[] { "Celeste" }, withoutRemovable.Games.Select(g => g.Name));

        var withRemovable = new FolderScanner().Scan(Ctx.Make(t, settings: new ScanSettings { ScanCommonGameFolders = true, IncludeRemovableDrives = true }, drives: drives));
        Assert.Equal(2, withRemovable.Games.Count);
    }
}

public class ExecutableFinderTests
{
    [Fact]
    public void Prefers_named_shipping_exe_over_helpers()
    {
        using var t = new TempDir();
        var dir = t.Dir("Stray");
        t.Exe("Stray/Stray.exe", 300_000);
        t.Exe("Stray/Hk_project/Binaries/Win64/Stray-Win64-Shipping.exe", 5_000_000);
        t.Exe("Stray/Engine/Binaries/ThirdParty/CEF3/Win64/UnrealCEFSubProcess.exe", 5_000_000);
        t.Exe("Stray/_CommonRedist/vcredist/vc_redist.x64.exe", 9_000_000);
        t.Exe("Stray/unins000.exe", 2_000_000);
        var exe = ExecutableFinder.FindMainExecutable(dir, "Stray");
        Assert.NotNull(exe);
        Assert.Contains("Stray", Path.GetFileName(exe));
        Assert.DoesNotContain("unins", exe);
        Assert.DoesNotContain("redist", exe!.ToLowerInvariant());
    }

    [Fact]
    public void Missing_folder_returns_null()
        => Assert.Null(ExecutableFinder.FindMainExecutable(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()), "x"));

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("EasyAntiCheat_Setup.exe", true)]
    [InlineData("Cyberpunk2077.exe", false)]
    [InlineData("eldenring.exe", false)]
    public void Exclusion_list(string name, bool excluded) => Assert.Equal(excluded, ExecutableFinder.IsExcludedExecutableName(name));
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
