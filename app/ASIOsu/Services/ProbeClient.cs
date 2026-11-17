using System.Diagnostics;
using System.Text.Json;

namespace ASIOsu.Services;

public sealed class ProbeClient
{
    private readonly string executable;

    public ProbeClient(string? toolsDirectory = null)
    {
        executable = Path.Combine(toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools"), "ASIOsu.Probe.exe");
    }

    public async Task<IReadOnlyList<AsioDriver>> ListAsync(CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<List<AsioDriver>>(await RunAsync(["list"], cancellationToken), JsonOptions) ?? [];

    public async Task<AsioDetails> InspectAsync(int device, CancellationToken cancellationToken = default)
        => JsonSerializer.Deserialize<AsioDetails>(await RunAsync(["inspect", device.ToString()], cancellationToken), JsonOptions)
           ?? throw new InvalidDataException("ASIO 정보를 읽지 못했습니다.");

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("ASIO probe가 없습니다.", executable);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("ASIO probe를 시작하지 못했습니다.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string result = await output;
        string diagnostic = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(diagnostic) ? "ASIO probe가 실패했습니다." : diagnostic.Trim());
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}

