using System.Security.Cryptography;
using System.Text.Json;

namespace ASIOsu.Services;

public sealed class InstallationManager
{
    private readonly string payload;
    private readonly string data;

    public InstallationManager(string? payloadDirectory = null, string? dataDirectory = null)
    {
        payload = Path.GetFullPath(payloadDirectory ?? Path.Combine(AppContext.BaseDirectory, "payload"));
        data = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu"));
    }

    public InstallSnapshot Inspect(GameLocation game)
    {
        if (!File.Exists(game.Executable)) return new(InstallState.Missing, "osu!lazer 실행 파일을 찾지 못했습니다.", false, false);
        string current = Path.Combine(game.Directory, "basswasapi.dll");
        string original = Path.Combine(game.Directory, "basswasapi_original.dll");
        string legacy = Path.Combine(game.Directory, "basswasapi_real.dll");
        string bridge = Path.Combine(payload, "basswasapi.dll");
        if (File.Exists(bridge) && SameFile(current, bridge) && PeInspector.IsVendorBass(original, "basswasapi.dll"))
        {
            bool complete = File.Exists(Path.Combine(game.Directory, "bassasio.dll")) && File.Exists(Path.Combine(game.Directory, "asiosu.ini"));
            return complete
                ? new(InstallState.Installed, "독립 ASIO 브리지가 설치되어 있습니다.", true, true)
                : new(InstallState.Damaged, "ASIO 브리지 파일이 일부 누락되었습니다.", true, true);
        }
        if (PeInspector.IsVendorBass(current, "basswasapi.dll"))
            return new(InstallState.Original, "원본 BASSWASAPI를 사용 중입니다.", true, false);
        if (PeInspector.IsVendorBass(legacy, "basswasapi.dll"))
            return new(InstallState.Legacy, "이전 프록시가 감지되었습니다. 설치 시 원본 BASS로 전환합니다.", true, true);
        return new(InstallState.Damaged, "검증할 수 없는 basswasapi.dll입니다.", false, PeInspector.IsVendorBass(original, "basswasapi.dll"));
    }

    public string CheckCompatibility(GameLocation game)
    {
        if (GameDiscovery.IsRunning(game)) throw new InvalidOperationException("먼저 osu!lazer를 종료하세요.");
        string source = FindWasapiOriginal(game) ?? throw new InvalidDataException("검증된 원본 basswasapi.dll을 찾지 못했습니다.");
        if (!PeInspector.IsVendorBass(source, "basswasapi.dll")) throw new InvalidDataException("원본 BASSWASAPI 검증에 실패했습니다.");
        VerifyWasapiSurface(source);
        VerifyWasapiSurface(RequirePayload("basswasapi.dll"));
        RequirePayload("bassasio.dll");
        return "원본 BASSWASAPI와 독립 ASIO 브리지 payload를 확인했습니다.";
    }

    public async Task InstallAsync(GameLocation game, RouteSettings route, CancellationToken cancellationToken = default)
    {
        CheckCompatibility(game);
        Directory.CreateDirectory(data);
        bool keepExistingRestorePoint = HasActiveRestorePoint(game);
        string transaction = Path.Combine(data, "backups", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        var record = new RestoreRecord(game.Directory, game.FrameworkIni, new List<RestoreFile>());
        string[] touched = ["bass.dll", "basswasapi.dll", "basswasapi_original.dll", "bassasio.dll", "asiosu.ini"];
        foreach (string name in touched) Backup(Path.Combine(game.Directory, name), transaction, record);
        Backup(game.FrameworkIni, transaction, record, "framework.ini");
        await File.WriteAllTextAsync(Path.Combine(transaction, "record.json"), JsonSerializer.Serialize(record, JsonOptions), cancellationToken);

        try
        {
            string originalWasapi = FindWasapiOriginal(game)!;
            string preservedWasapi = Path.Combine(game.Directory, "basswasapi_original.dll");
            if (!SamePath(originalWasapi, preservedWasapi)) File.Copy(originalWasapi, preservedWasapi, true);

            string? originalBass = FindBassOriginal(game);
            if (originalBass is not null && !PeInspector.IsVendorBass(Path.Combine(game.Directory, "bass.dll"), "bass.dll"))
                File.Copy(originalBass, Path.Combine(game.Directory, "bass.dll"), true);

            File.Copy(RequirePayload("basswasapi.dll"), Path.Combine(game.Directory, "basswasapi.dll"), true);
            File.Copy(RequirePayload("bassasio.dll"), Path.Combine(game.Directory, "bassasio.dll"), true);
            WriteRoute(game, route, true);

            IniDocument framework = IniDocument.Load(game.FrameworkIni);
            framework.Write("AudioUseExperimentalWasapi", "True");
            framework.Save(game.FrameworkIni);
            if (!keepExistingRestorePoint)
                await File.WriteAllTextAsync(Path.Combine(data, "active-backup.txt"), transaction, cancellationToken);
        }
        catch
        {
            RestoreRecordFiles(record, transaction);
            throw;
        }
    }

    public async Task RestoreAsync(GameLocation game, CancellationToken cancellationToken = default)
    {
        if (GameDiscovery.IsRunning(game)) throw new InvalidOperationException("먼저 osu!lazer를 종료하세요.");
        string? originalWasapi = FindWasapiOriginal(game);
        if (originalWasapi is null) throw new InvalidDataException("복원할 원본 basswasapi.dll을 찾지 못했습니다.");
        File.Copy(originalWasapi, Path.Combine(game.Directory, "basswasapi.dll"), true);
        string? originalBass = FindBassOriginal(game);
        if (originalBass is not null && !PeInspector.IsVendorBass(Path.Combine(game.Directory, "bass.dll"), "bass.dll"))
            File.Copy(originalBass, Path.Combine(game.Directory, "bass.dll"), true);

        string pointer = Path.Combine(data, "active-backup.txt");
        if (File.Exists(pointer))
        {
            string backupRoot = Path.GetFullPath(Path.Combine(data, "backups")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string transaction = Path.GetFullPath((await File.ReadAllTextAsync(pointer, cancellationToken)).Trim());
            if (!transaction.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("복원 기록 경로가 백업 폴더를 벗어났습니다.");
            string recordPath = Path.Combine(transaction, "record.json");
            if (File.Exists(recordPath))
            {
                RestoreRecord? record = JsonSerializer.Deserialize<RestoreRecord>(await File.ReadAllTextAsync(recordPath, cancellationToken), JsonOptions);
                if (record is not null)
                {
                    if (!Path.GetFullPath(record.GameDirectory).Equals(Path.GetFullPath(game.Directory), StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetFullPath(record.FrameworkIni).Equals(Path.GetFullPath(game.FrameworkIni), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("복원 기록이 현재 게임 폴더와 일치하지 않습니다.");
                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        Path.GetFullPath(game.FrameworkIni),
                        Path.GetFullPath(Path.Combine(game.Directory, "bassasio.dll")),
                        Path.GetFullPath(Path.Combine(game.Directory, "asiosu.ini")),
                        Path.GetFullPath(Path.Combine(game.Directory, "basswasapi_original.dll"))
                    };
                    foreach (RestoreFile file in record.Files.Where(file => allowed.Contains(Path.GetFullPath(file.Destination))))
                        RestoreOne(file, transaction);
                }
            }
        }
        DeleteIfExists(pointer);
    }

    public RouteSettings LoadRoute(GameLocation game)
    {
        string path = Path.Combine(game.Directory, "asiosu.ini");
        if (!File.Exists(path)) path = Path.Combine(data, "route.ini");
        var ini = new System.Collections.Specialized.NameValueCollection();
        foreach (string line in File.Exists(path) ? File.ReadAllLines(path) : [])
        {
            int equals = line.IndexOf('=');
            if (equals > 0) ini[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return new(ini["Driver"] ?? "", Parse(ini["FirstOutput"], 0), Parse(ini["SampleRate"], 48000), Parse(ini["BufferFrames"], 32));
    }

    public void WriteRoute(GameLocation game, RouteSettings route, bool installed)
    {
        if (string.IsNullOrWhiteSpace(route.Driver)) throw new InvalidOperationException("ASIO 드라이버를 선택하세요.");
        if (route.FirstOutput < 0 || route.SampleRate is < 8000 or > 384000 || route.BufferFrames is < 1 or > 8192)
            throw new InvalidOperationException("오디오 경로 값이 허용 범위를 벗어났습니다.");
        if (installed && GameDiscovery.IsRunning(game)) throw new InvalidOperationException("설정을 쓰기 전에 osu!lazer를 종료하세요.");
        Directory.CreateDirectory(data);
        string path = installed ? Path.Combine(game.Directory, "asiosu.ini") : Path.Combine(data, "route.ini");
        string content = $"[ASIO]\r\nEnabled=1\r\nDriver={route.Driver}\r\nFirstOutput={route.FirstOutput}\r\nSampleRate={route.SampleRate}\r\nBufferFrames={route.BufferFrames}\r\n";
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }

    private string RequirePayload(string name)
    {
        string path = Path.Combine(payload, name);
        return File.Exists(path) ? path : throw new FileNotFoundException($"payload 파일이 없습니다: {name}", path);
    }

    private static string? FindWasapiOriginal(GameLocation game)
    {
        string[] names = ["basswasapi_original.dll", "basswasapi.dll", "basswasapi_real.dll"];
        return names.Select(name => Path.Combine(game.Directory, name)).FirstOrDefault(path => PeInspector.IsVendorBass(path, "basswasapi.dll"));
    }

    private static string? FindBassOriginal(GameLocation game)
    {
        string[] names = ["bass.dll", "bass_real.dll"];
        return names.Select(name => Path.Combine(game.Directory, name)).FirstOrDefault(path => PeInspector.IsVendorBass(path, "bass.dll"));
    }

    private bool HasActiveRestorePoint(GameLocation game)
    {
        try
        {
            string pointer = Path.Combine(data, "active-backup.txt");
            if (!File.Exists(pointer)) return false;
            string backupRoot = Path.GetFullPath(Path.Combine(data, "backups")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string transaction = Path.GetFullPath(File.ReadAllText(pointer).Trim());
            if (!transaction.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)) return false;
            string recordPath = Path.Combine(transaction, "record.json");
            if (!File.Exists(recordPath)) return false;
            RestoreRecord? record = JsonSerializer.Deserialize<RestoreRecord>(File.ReadAllText(recordPath), JsonOptions);
            return record is not null && SamePath(record.GameDirectory, game.Directory) && SamePath(record.FrameworkIni, game.FrameworkIni);
        }
        catch
        {
            return false;
        }
    }

    private static void Backup(string destination, string backup, RestoreRecord record, string? backupName = null)
    {
        string name = backupName ?? Path.GetFileName(destination);
        string stored = Path.Combine(backup, name);
        bool exists = File.Exists(destination);
        string? hash = null;
        if (exists)
        {
            File.Copy(destination, stored, true);
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stored)));
        }
        record.Files.Add(new(destination, name, exists, hash));
    }

    private static void RestoreRecordFiles(RestoreRecord record, string backup)
    {
        foreach (RestoreFile file in record.Files) RestoreOne(file, backup);
    }

    private static void RestoreOne(RestoreFile file, string backup)
    {
        if (!file.Existed) { DeleteIfExists(file.Destination); return; }
        string source = Path.Combine(backup, file.BackupName);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("복원 백업 SHA-256이 일치하지 않습니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
        File.Copy(source, file.Destination, true);
    }

    private static bool SameFile(string left, string right) => File.Exists(left) && File.Exists(right) &&
        new FileInfo(left).Length == new FileInfo(right).Length &&
        SHA256.HashData(File.ReadAllBytes(left)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(right)));
    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static int Parse(string? value, int fallback) => int.TryParse(value, out int result) ? result : fallback;
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }

    private static void VerifyWasapiSurface(string path)
    {
        ExportSurface surface = PeExports.Read(path);
        if (surface.Machine != 0x8664 || surface.OrdinalBase != 1 || surface.Names.Count != PeInspector.RequiredWasapiExports.Count)
            throw new InvalidDataException($"{Path.GetFileName(path)}의 x64 export 개수가 맞지 않습니다.");
        for (int index = 0; index < PeInspector.RequiredWasapiExports.Count; index++)
            if (!surface.Names.TryGetValue((ushort)(index + 1), out string? name) || name != PeInspector.RequiredWasapiExports[index])
                throw new InvalidDataException($"{Path.GetFileName(path)} export ordinal {index + 1}이 호환되지 않습니다.");
    }

    private sealed record RestoreRecord(string GameDirectory, string FrameworkIni, List<RestoreFile> Files);
    private sealed record RestoreFile(string Destination, string BackupName, bool Existed, string? Sha256);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

