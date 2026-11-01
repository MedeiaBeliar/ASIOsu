namespace ASIOsu;

public enum InstallState { Missing, Original, Installed, Legacy, Damaged }

public sealed record GameLocation(string Directory, string Executable, string FrameworkIni);

public sealed record InstallSnapshot(InstallState State, string Summary, bool CanInstall, bool CanRestore);

public sealed record AsioDriver(int Index, string Name)
{
    public override string ToString() => Name;
}

public sealed record AsioChannel(int Index, string Name);

public sealed record AsioDetails(
    string Driver,
    int Inputs,
    int Outputs,
    int MinimumBuffer,
    int MaximumBuffer,
    int PreferredBuffer,
    int Granularity,
    IReadOnlyList<int> SupportedRates,
    IReadOnlyList<AsioChannel> OutputChannels);

public sealed record StereoPair(int FirstChannel, string Label)
{
    public override string ToString() => Label;
}

public sealed record RouteSettings(string Driver, int FirstOutput, int SampleRate, int BufferFrames);

public sealed record ReleaseInfo(Version Version, string Tag, Uri AssetUrl, Uri PageUrl, long Size, string Sha256);

public sealed record ReleaseProgress(string Text, int? Percent = null);

public sealed record PreparedRelease(ReleaseInfo Release, string PackageDirectory);


