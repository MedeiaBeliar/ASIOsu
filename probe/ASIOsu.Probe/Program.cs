using System.Runtime.InteropServices;
using System.Text.Json;

Native.SetUnicode(true);
try
{
    if (args.Length == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        var drivers = new List<object>();
        for (uint index = 0; index < 1024; index++)
        {
            if (!Native.GetDeviceInfo(index, out DeviceInfo info)) break;
            drivers.Add(new { Index = (int)index, Name = Marshal.PtrToStringUni(info.Name) ?? $"ASIO {index}" });
        }
        Console.WriteLine(JsonSerializer.Serialize(drivers));
        return;
    }
    if (args.Length == 2 && args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], out int device))
    {
        if (!Native.Init(device, 1)) throw new InvalidOperationException($"ASIO 드라이버를 열지 못했습니다. 코드: {Native.ErrorGetCode()}");
        try
        {
            if (!Native.GetInfo(out DriverInfo info)) throw new InvalidOperationException("ASIO 드라이버 정보를 읽지 못했습니다.");
            var outputs = new List<object>();
            for (uint channel = 0; channel < info.Outputs; channel++)
            {
                if (!Native.ChannelGetInfo(false, channel, out ChannelInfo detail)) continue;
                outputs.Add(new { Index = (int)channel, Name = detail.Name ?? $"Output {channel + 1}" });
            }
            int[] supportedRates = [44100, 48000, 88200, 96000, 176400, 192000];
            supportedRates = supportedRates.Where(rate => Native.CheckRate(rate)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Driver = info.Name ?? "ASIO",
                Inputs = (int)info.Inputs,
                Outputs = (int)info.Outputs,
                MinimumBuffer = (int)info.BufferMinimum,
                MaximumBuffer = (int)info.BufferMaximum,
                PreferredBuffer = (int)info.BufferPreferred,
                Granularity = info.BufferGranularity,
                SupportedRates = supportedRates,
                OutputChannels = outputs
            }));
        }
        finally { Native.Free(); }
        return;
    }
    throw new InvalidOperationException("잘못된 probe 명령입니다.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceInfo { public IntPtr Name; public IntPtr Driver; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct DriverInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Name;
    public uint Version, Inputs, Outputs, BufferMinimum, BufferMaximum, BufferPreferred;
    public int BufferGranularity;
    public uint InitFlags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct ChannelInfo
{
    public uint Group, Format;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Name;
}

internal static class Native
{
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_SetUnicode")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetUnicode([MarshalAs(UnmanagedType.Bool)] bool unicode);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ErrorGetCode")] internal static extern uint ErrorGetCode();
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_GetDeviceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetDeviceInfo(uint device, out DeviceInfo info);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Init")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Init(int device, uint flags);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_Free")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Free();
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_GetInfo")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetInfo(out DriverInfo info);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_CheckRate")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CheckRate(double rate);
    [DllImport("bassasio.dll", EntryPoint = "BASS_ASIO_ChannelGetInfo")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ChannelGetInfo([MarshalAs(UnmanagedType.Bool)] bool input, uint channel, out ChannelInfo info);
}

