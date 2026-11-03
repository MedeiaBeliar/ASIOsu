using ASIOsu;
using ASIOsu.Services;
using System.Security.Cryptography;
using System.Text.Json;

int assertions = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    assertions++;
}

string root = Path.Combine(Path.GetTempPath(), "ASIOsu-independent-" + Guid.NewGuid().ToString("N"));
string gameDirectory = Path.Combine(root, "game");
string payload = Path.Combine(root, "payload");
string data = Path.Combine(root, "data");
Directory.CreateDirectory(gameDirectory);
Directory.CreateDirectory(payload);
try
{
    string repository = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new Exception("Repository path argument is required.");
    string realBass = @"C:\Users\Hebi\AppData\Local\osulazer\current\bass_real.dll";
    string realWasapi = @"C:\Users\Hebi\AppData\Local\osulazer\current\basswasapi_real.dll";
    if (!File.Exists(realBass) || !File.Exists(realWasapi)) throw new Exception("Verified local BASS originals are unavailable.");

    File.WriteAllBytes(Path.Combine(gameDirectory, "osu!.exe"), [0x4d, 0x5a]);
    File.Copy(realBass, Path.Combine(gameDirectory, "bass.dll"));
    File.Copy(realWasapi, Path.Combine(gameDirectory, "basswasapi.dll"));
    File.Copy(Path.Combine(repository, "artifacts", "native", "basswasapi.dll"), Path.Combine(payload, "basswasapi.dll"));
    File.Copy(Path.Combine(repository, "artifacts", "native", "bassasio.dll"), Path.Combine(payload, "bassasio.dll"));
    string framework = Path.Combine(root, "framework.ini");
    string originalFramework = "AudioDevice = Existing Windows Device\r\nAudioUseExperimentalWasapi = False\r\nVolumeUniversal = 0.3\r\n";
    File.WriteAllText(framework, originalFramework);
    var game = new GameLocation(gameDirectory, Path.Combine(gameDirectory, "osu!.exe"), framework);
    var manager = new InstallationManager(payload, data);

    Check(manager.Inspect(game).State == InstallState.Original, "original state");
    Check(manager.CheckCompatibility(game).Length > 0, "compatibility");
    var route = new RouteSettings("ASIO 2.0 - Test", 2, 48000, 32);
    await manager.InstallAsync(game, route);
    Check(manager.Inspect(game).State == InstallState.Installed, "installed state");
    Check(Same(Path.Combine(gameDirectory, "bass.dll"), realBass), "bass.dll remains original");
    Check(Same(Path.Combine(gameDirectory, "basswasapi_original.dll"), realWasapi), "original BASSWASAPI preserved");
    Check(Same(Path.Combine(gameDirectory, "basswasapi.dll"), Path.Combine(payload, "basswasapi.dll")), "independent bridge installed");
    string configured = File.ReadAllText(framework);
    Check(configured.Contains("AudioDevice = Existing Windows Device"), "audio device preserved");
    Check(configured.Contains("AudioUseExperimentalWasapi = True"), "experimental callback enabled");
    Check(manager.LoadRoute(game) == route, "route round-trip");

    string firstRestorePoint = File.ReadAllText(Path.Combine(data, "active-backup.txt"));
    await manager.InstallAsync(game, route);
    Check(manager.Inspect(game).State == InstallState.Installed, "installed update state");
    Check(File.ReadAllText(Path.Combine(data, "active-backup.txt")) == firstRestorePoint, "installed update retains original restore point");

    await manager.RestoreAsync(game);
    Check(manager.Inspect(game).State == InstallState.Original, "restored state");
    Check(Same(Path.Combine(gameDirectory, "basswasapi.dll"), realWasapi), "BASSWASAPI original restored");
    Check(File.ReadAllText(framework) == originalFramework, "framework restored byte-for-byte");
    Check(!File.Exists(Path.Combine(gameDirectory, "asiosu.ini")), "new configuration removed");

    var ini = IniDocument.Load(framework);
    Check(ini.Read("AudioDevice") == "Existing Windows Device", "INI parser");

    string releaseJson = """
    {"tag_name":"v9.8.7","html_url":"https://github.com/MedeiaBeliar/ASIOsu/releases/tag/v9.8.7",
     "draft":false,"prerelease":false,"assets":[{"name":"ASIOsu-win-x64.zip","state":"uploaded","size":12345,
     "digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
     "browser_download_url":"https://github.com/MedeiaBeliar/ASIOsu/releases/download/v9.8.7/ASIOsu-win-x64.zip"}]}
    """;
    ReleaseInfo release = ReleaseUpdater.Parse(releaseJson, new Version(1, 0, 0)) ?? throw new Exception("FAIL: update parse");
    Check(release.Version == new Version(9, 8, 7), "release version parse");
    Check(release.Size == 12345 && release.Sha256.Length == 64, "release digest parse");
    Check(ReleaseUpdater.Parse(releaseJson, new Version(9, 8, 7)) is null, "current release ignored");

    string updatePackage = Path.Combine(root, "update-package");
    Directory.CreateDirectory(Path.Combine(updatePackage, "runtime"));
    var packageFiles = new Dictionary<string, byte[]>
    {
        ["ASIOsu.exe"] = [1, 2, 3],
        [@"runtime\ASIOsu.exe"] = [4, 5, 6],
        [@"runtime\ASIOsu.dll"] = [7, 8, 9],
        [@"runtime\ASIOsu.pri"] = [10, 11, 12]
    };
    foreach ((string path, byte[] bytes) in packageFiles) File.WriteAllBytes(Path.Combine(updatePackage, path), bytes);
    File.WriteAllText(Path.Combine(updatePackage, "manifest.json"), JsonSerializer.Serialize(new
    {
        product = "ASIOsu", version = "9.8.7",
        files = packageFiles.Select(item => new { path = item.Key, size = item.Value.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(item.Value)) })
    }));
    ReleaseUpdater.VerifyPackage(updatePackage, release);
    Check(true, "update manifest verification");
    File.WriteAllBytes(Path.Combine(updatePackage, "runtime", "ASIOsu.dll"), [0]);
    try { ReleaseUpdater.VerifyPackage(updatePackage, release); throw new Exception("FAIL: tampered update accepted"); }
    catch (InvalidDataException) { Check(true, "tampered update rejected"); }
    if (args.Contains("--download-latest", StringComparer.OrdinalIgnoreCase))
    {
        var liveUpdater = new ReleaseUpdater(new Version(0, 0, 0));
        ReleaseInfo latest = await liveUpdater.CheckAsync() ?? throw new Exception("FAIL: latest release unavailable");
        PreparedRelease prepared = await liveUpdater.DownloadAsync(latest);
        Check(File.Exists(Path.Combine(prepared.PackageDirectory, "manifest.json")), "live release download");
        Console.WriteLine($"RELEASE: {latest.Tag} {latest.Size} bytes {latest.Sha256}");
    }
    Console.WriteLine($"PASS: {assertions} independent core assertions");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static bool Same(string left, string right) =>
    SHA256.HashData(File.ReadAllBytes(left)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(right)));

