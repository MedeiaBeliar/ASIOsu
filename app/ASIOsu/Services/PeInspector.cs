using System.Diagnostics;

namespace ASIOsu.Services;

public static class PeInspector
{
    private static readonly string[] requiredWasapiExports =
    [
        "BASS_WASAPI_CheckFormat", "BASS_WASAPI_Free", "BASS_WASAPI_GetCPU", "BASS_WASAPI_GetData",
        "BASS_WASAPI_GetDevice", "BASS_WASAPI_GetDeviceInfo", "BASS_WASAPI_GetDeviceLevel", "BASS_WASAPI_GetInfo",
        "BASS_WASAPI_GetLevel", "BASS_WASAPI_GetLevelEx", "BASS_WASAPI_GetMute", "BASS_WASAPI_GetVersion",
        "BASS_WASAPI_GetVolume", "BASS_WASAPI_Init", "BASS_WASAPI_IsStarted", "BASS_WASAPI_Lock",
        "BASS_WASAPI_PutData", "BASS_WASAPI_SetDevice", "BASS_WASAPI_SetMute", "BASS_WASAPI_SetNotify",
        "BASS_WASAPI_SetVolume", "BASS_WASAPI_Start", "BASS_WASAPI_Stop"
    ];

    public static bool IsVendorBass(string path, string expectedName)
    {
        if (!File.Exists(path)) return false;
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
        return version.CompanyName?.Contains("Un4seen", StringComparison.OrdinalIgnoreCase) == true &&
               version.FileMajorPart == 2 && version.FileMinorPart == 4;
    }

    public static IReadOnlyList<string> RequiredWasapiExports => requiredWasapiExports;
}

