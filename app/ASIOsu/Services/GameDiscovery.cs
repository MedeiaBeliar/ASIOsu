using Microsoft.Win32;

namespace ASIOsu.Services;

public static class GameDiscovery
{
    public static GameLocation Find(string? selected = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(selected)) candidates.Add(selected);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osulazer", "current"));
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\osu\shell\open\command"))
        {
            string? command = key?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(command))
            {
                string executable = command.Trim().Trim('"').Split('"')[0];
                string? root = Directory.GetParent(executable)?.FullName;
                if (root is not null) candidates.Add(Path.Combine(root, "current"));
            }
        }

        string? directory = candidates.Select(Path.GetFullPath).FirstOrDefault(path => File.Exists(Path.Combine(path, "osu!.exe")));
        if (directory is null) directory = Path.GetFullPath(selected ?? candidates[0]);
        string framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu", "framework.ini");
        return new(directory, Path.Combine(directory, "osu!.exe"), framework);
    }

    public static bool IsRunning(GameLocation game)
    {
        string expected = Path.GetFullPath(game.Executable);
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("osu!"))
        {
            try { if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
            finally { process.Dispose(); }
        }
        return false;
    }
}

