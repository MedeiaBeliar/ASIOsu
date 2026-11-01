using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASIOsu.Services;

public sealed class ReleaseUpdater
{
    public const string AssetName = "ASIOsu-win-x64.zip";
    private static readonly Uri LatestEndpoint = new("https://api.github.com/repos/MedeiaBeliar/ASIOsu/releases/latest");
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly Version current;
    private readonly HttpClient client;

    public string? PortableRoot { get; }
    public string CurrentText => Format(current);
    public bool CanInstall => PortableRoot is not null;

    public ReleaseUpdater(Version version, string? baseDirectory = null, HttpClient? client = null)
    {
        current = Normalize(version);
        this.client = client ?? SharedClient;
        PortableRoot = FindPortableRoot(baseDirectory ?? AppContext.BaseDirectory);
    }

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestEndpoint);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token);
        return Parse(json, current);
    }

    public static ReleaseInfo? Parse(string json, Version currentVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("안정 릴리스가 아닙니다.");
        string tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("릴리스 태그가 없습니다.");
        Version version = ParseVersion(tag);
        Uri page = RequireGithub(root.GetProperty("html_url").GetString());
        JsonElement asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("name").GetString() == AssetName && item.GetProperty("state").GetString() == "uploaded");
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"릴리스에 {AssetName}이 없습니다.");
        long size = asset.GetProperty("size").GetInt64();
        if (size is <= 0 or > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("릴리스 파일 크기가 올바르지 않습니다.");
        string digest = asset.GetProperty("digest").GetString() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("GitHub SHA-256 digest가 없습니다.");
        Uri assetUrl = RequireGithub(asset.GetProperty("browser_download_url").GetString());
        var result = new ReleaseInfo(version, tag, assetUrl, page, size, digest[7..].ToUpperInvariant());
        return version > Normalize(currentVersion) ? result : null;
    }

    public async Task<PreparedRelease> DownloadAsync(ReleaseInfo release, IProgress<ReleaseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu", "downloads");
        Directory.CreateDirectory(downloads);
        string root = SafeTagDirectory(downloads, release.Tag);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        string partial = Path.Combine(root, AssetName + ".partial");
        string archive = Path.Combine(root, AssetName);
        progress?.Report(new("다운로드 준비 중", 0));
        using HttpResponseMessage response = await DownloadResponseAsync(release.AssetUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long expected && expected != release.Size)
            throw new InvalidDataException("GitHub 릴리스 크기가 변경되었습니다.");

        long received = 0;
        string actual;
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 1024];
            int length;
            while ((length = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += length;
                if (received > release.Size) throw new InvalidDataException("다운로드가 예상 크기를 초과했습니다.");
                hash.AppendData(buffer, 0, length);
                await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                progress?.Report(new("업데이트 다운로드 중", (int)(received * 100 / release.Size)));
            }
            await output.FlushAsync(cancellationToken);
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (received != release.Size || !actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("업데이트 SHA-256 검증에 실패했습니다.");
        File.Move(partial, archive);

        progress?.Report(new("패키지 검증 중"));
        string temporary = Path.Combine(root, "package.tmp");
        string package = Path.Combine(root, "package");
        Extract(archive, temporary);
        VerifyPackage(temporary, release);
        Directory.Move(temporary, package);
        return new(release, package);
    }

    public void Apply(PreparedRelease prepared)
    {
        string target = PortableRoot ?? throw new InvalidOperationException("portable 패키지에서 실행해야 자동 설치할 수 있습니다.");
        string launcher = Path.Combine(target, "ASIOsu.exe");
        if (!File.Exists(launcher)) throw new FileNotFoundException("루트 ASIOsu.exe가 없습니다.", launcher);
        string writeTest = Path.Combine(target, ".asiosu-write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(writeTest, []); }
        finally { if (File.Exists(writeTest)) File.Delete(writeTest); }
        string hostDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu", "update-host");
        Directory.CreateDirectory(hostDirectory);
        string helper = Path.Combine(hostDirectory, "ASIOsu.UpdateHost." + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(launcher, helper);
        var start = new ProcessStartInfo { FileName = helper, UseShellExecute = false };
        foreach (string argument in new[] { "--apply-update", "--source", prepared.PackageDirectory, "--target", target,
                     "--wait-pid", Environment.ProcessId.ToString(), "--tag", prepared.Release.Tag }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("업데이트 host를 시작하지 못했습니다.");
    }

    public static void CleanupHosts()
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu", "update-host");
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.EnumerateFiles(directory, "ASIOsu.UpdateHost.*.exe"))
                try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5)) File.Delete(file); } catch { }
        }
        catch { }
    }

    public static void VerifyPackage(string packageDirectory, ReleaseInfo release)
    {
        string root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, "manifest.json")), JsonOptions)
                            ?? throw new InvalidDataException("manifest.json을 읽지 못했습니다.");
        if (manifest.Product != "ASIOsu" || ParseVersion(manifest.Version) != release.Version || manifest.Files.Count is 0 or > 5000)
            throw new InvalidDataException("패키지 manifest가 릴리스와 일치하지 않습니다.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestEntry entry in manifest.Files)
        {
            string path = ResolveInside(root, entry.Path);
            string relative = Path.GetRelativePath(root, path);
            if (!expected.Add(relative)) throw new InvalidDataException("manifest에 중복 파일이 있습니다.");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Size) throw new InvalidDataException($"업데이트 파일 크기가 다릅니다: {entry.Path}");
            using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"업데이트 파일 digest가 다릅니다: {entry.Path}");
        }
        foreach (string required in new[] { "ASIOsu.exe", @"runtime\ASIOsu.exe", @"runtime\ASIOsu.dll", @"runtime\ASIOsu.pri" })
            if (!expected.Contains(required)) throw new InvalidDataException($"필수 파일이 없습니다: {required}");
        string[] actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file)).Where(file => !file.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (actual.Length != expected.Count || actual.Any(file => !expected.Contains(file)))
            throw new InvalidDataException("manifest에 없는 파일이 포함되어 있습니다.");
    }

    private async Task<HttpResponseMessage> DownloadResponseAsync(Uri initial, CancellationToken cancellationToken)
    {
        Uri current = initial;
        for (int count = 0; count <= 5; count++)
        {
            if (!AllowedDownload(current)) throw new InvalidDataException("허용되지 않은 다운로드 주소입니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("GitHub 이동 주소가 없습니다.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new InvalidDataException("GitHub 다운로드 이동 횟수가 너무 많습니다.");
    }

    private static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 5000) throw new InvalidDataException("ZIP 파일 수가 올바르지 않습니다.");
        long expanded = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("ZIP 압축 해제 크기가 너무 큽니다.");
            string path = ResolveInside(root, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("패키지 경로가 올바르지 않습니다.");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("패키지 경로가 루트를 벗어났습니다.");
        return path;
    }

    private static string SafeTagDirectory(string parent, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("릴리스 태그를 경로로 사용할 수 없습니다.");
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, tag));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("릴리스 경로가 저장소를 벗어났습니다.");
        return path;
    }

    private static Version ParseVersion(string tag)
    {
        string value = tag.TrimStart('v', 'V');
        if (!Version.TryParse(value, out Version? version) || version.Build < 0 || version.Revision >= 0)
            throw new InvalidDataException($"지원하지 않는 버전입니다: {tag}");
        return Normalize(version);
    }
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    private static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    private static Uri RequireGithub(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
        ? uri : throw new InvalidDataException("GitHub 릴리스 URL이 올바르지 않습니다.");
    private static bool AllowedDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    private static string? FindPortableRoot(string baseDirectory)
    {
        var runtime = new DirectoryInfo(Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar));
        DirectoryInfo? root = runtime.Name.Equals("runtime", StringComparison.OrdinalIgnoreCase) ? runtime.Parent : null;
        return root is not null && File.Exists(Path.Combine(root.FullName, "ASIOsu.exe")) && File.Exists(Path.Combine(runtime.FullName, "ASIOsu.exe")) ? root.FullName : null;
    }
    private static HttpClient CreateClient()
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        result.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ASIOsu", "1.0"));
        result.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        result.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return result;
    }

    private sealed class Manifest
    {
        [JsonPropertyName("product")] public string Product { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("files")] public List<ManifestEntry> Files { get; set; } = [];
    }
    private sealed class ManifestEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

