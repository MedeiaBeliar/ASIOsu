using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ASIOsu.Services;

public sealed class StableAudio
{
    public static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "stable");
    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu-stable");
    private static string Preferences => Path.Combine(DataDirectory, "integrated.json");
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");

    private static string Config(string folder) => Path.Combine(folder, $"osu!.{Environment.UserName}.cfg");

    public static (string Folder, RouteSettings Route) Load()
    {
        if (File.Exists(Preferences))
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Preferences))
                ?? throw new InvalidDataException("stable 설정을 읽지 못했습니다.");
            return (settings.Folder, settings.Route);
        }
        IniDocument previous = IniDocument.Load(Path.Combine(DataDirectory, "route.ini"));
        return (DefaultFolder, new(previous.Read("Driver") ?? "", Number("FirstOutput", 0),
            Number("SampleRate", 48000), Number("BufferFrames", 32)));

        int Number(string key, int fallback) => int.TryParse(previous.Read(key), out int value) ? value : fallback;
    }

    public static void Save(string folder, RouteSettings route)
    {
        Directory.CreateDirectory(DataDirectory);
        WriteAtomic(Preferences, JsonSerializer.SerializeToUtf8Bytes(new Settings(folder, route)));
    }

    public static string? ReadDevice(string folder)
    {
        string config = Config(folder);
        if (!File.Exists(config)) return null;
        var (content, _, _) = ReadConfig(config);
        return DeviceLine.Match(content).Groups[1].Value.Trim();
    }

    public static void SaveDevice(string folder, string name)
    {
        string exe = Path.Combine(folder, "osu!.exe");
        if (!File.Exists(exe) || !File.Exists(Config(folder)))
            throw new InvalidOperationException("이 폴더에서 osu!stable을 한 번 실행한 뒤 종료하세요.");
        if (GameDiscovery.IsRunning(new(folder, exe, "")))
            throw new InvalidOperationException("출력 장치를 저장하려면 osu!stable을 종료하세요.");
        if (name.Contains('\r') || name.Contains('\n'))
            throw new ArgumentException("출력 장치 이름에 줄바꿈이 있습니다.", nameof(name));
        string config = Config(folder);
        var (content, encoding, bom) = ReadConfig(config);
        if (DeviceLine.Match(content).Groups[1].Value.Trim() == name) return;
        string updated = DeviceLine.IsMatch(content)
            ? DeviceLine.Replace(content, _ => "AudioDevice = " + name, 1)
            : content + (content.Length == 0 || content.EndsWith('\n') ? "" : LineEnding(content))
                + "AudioDevice = " + name + LineEnding(content);
        WriteAtomic(config, [.. bom, .. encoding.GetBytes(updated)]);
    }

    public static Process Connect(string folder, RouteSettings route)
    {
        string executable = Path.Combine(folder, "osu!.exe");
        Process[] processes = Process.GetProcessesByName("osu!");
        try
        {
            foreach (Process process in processes)
            {
                bool matches;
                try
                {
                    matches = !process.HasExited && process.MainWindowHandle != IntPtr.Zero
                        && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
                }
                catch (System.ComponentModel.Win32Exception) { continue; }
                catch (InvalidOperationException) { continue; }
                if (matches) return StartCapture(process.Id, route);
            }
        }
        finally { foreach (Process process in processes) process.Dispose(); }
        throw new InvalidOperationException("선택한 폴더의 osu!stable을 먼저 실행하세요.");
    }

    private static Process StartCapture(int pid, RouteSettings route)
    {
        var start = new ProcessStartInfo(Path.Combine(ToolsDirectory, "ASIOsu.Loopback.exe"))
        {
            WorkingDirectory = ToolsDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { pid.ToString(CultureInfo.InvariantCulture), route.Driver,
            route.FirstOutput.ToString(CultureInfo.InvariantCulture), route.SampleRate.ToString(CultureInfo.InvariantCulture),
            route.BufferFrames.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("stable 캡처를 시작하지 못했습니다.");
    }

    private static readonly Regex DeviceLine = new(@"^[ \t]*AudioDevice[ \t]*=[ \t]*([^\r\n]*)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string LineEnding(string content) => content.Contains("\r\n") ? "\r\n" : "\n";

    private static (string Text, Encoding Encoding, byte[] Bom) ReadConfig(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()) ? new UnicodeEncoding(false, true, true)
            : bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()) ? new UnicodeEncoding(true, true, true)
            : new UTF8Encoding(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), true);
        byte[] bom = encoding.GetPreamble();
        return (encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length), encoding, bom);
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { File.Delete(temp); }
    }

    private sealed record Settings(string Folder, RouteSettings Route);
}
