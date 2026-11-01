namespace ASIOsu.Services;

public sealed class IniDocument
{
    private readonly List<string> lines;

    private IniDocument(IEnumerable<string> lines) => this.lines = lines.ToList();

    public static IniDocument Load(string path) => new(File.Exists(path) ? File.ReadAllLines(path) : []);

    public string? Read(string key)
    {
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator < 0 || !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(separator + 1)..].Trim();
        }
        return null;
    }

    public void Write(string key, string value)
    {
        for (int index = 0; index < lines.Count; index++)
        {
            int separator = lines[index].IndexOf('=');
            if (separator < 0 || !lines[index][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"{key} = {value}";
            return;
        }
        lines.Add($"{key} = {value}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".asiosu.tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }
}

