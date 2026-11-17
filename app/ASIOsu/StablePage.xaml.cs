using System.Diagnostics;
using ASIOsu.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Pickers;

namespace ASIOsu;

public sealed partial class StablePage : UserControl
{
    private readonly ProbeClient probe = new(StableAudio.ToolsDirectory);
    private string folder = StableAudio.DefaultFolder;
    private RouteSettings saved = new("", 0, 48000, 32);
    private AsioDetails? details;
    private Process? capture;
    private bool loaded;
    private bool loading;
    private bool starting;
    private bool updatingDrivers;

    public nint WindowHandle { get; set; }

    public StablePage() => InitializeComponent();

    public async void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try { (folder, saved) = StableAudio.Load(); }
        catch (Exception error) { Report(CaptureInfo, InfoBarSeverity.Error, "저장된 설정을 불러오지 못했습니다", error.Message); }
        await ReloadAsync();
    }

    private async void Reload(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        if (loading || starting || capture is not null) return;
        loading = true;
        UpdateControls();
        try
        {
            await LoadDevicesAsync();
            IReadOnlyList<AsioDriver> drivers = await probe.ListAsync();
            updatingDrivers = true;
            try
            {
                DriverCombo.ItemsSource = drivers;
                DriverCombo.SelectedItem = drivers.FirstOrDefault(driver => driver.Name == saved.Driver)
                    ?? (drivers.Count == 1 ? drivers[0] : null);
            }
            finally { updatingDrivers = false; }
            await LoadDriverAsync();
        }
        catch (Exception error) { Report(CaptureInfo, InfoBarSeverity.Error, "장치를 읽지 못했습니다", error.Message); }
        finally { loading = false; UpdateControls(); }
    }

    private async Task LoadDevicesAsync()
    {
        FolderText.Text = folder;
        string? current = StableAudio.ReadDevice(folder);
        var devices = new List<DeviceChoice> { new("", "Windows 기본 장치") };
        DeviceInformationCollection outputs = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        foreach (string name in outputs.Where(device => device.IsEnabled).Select(device => device.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).Order())
            devices.Add(new(name, name));
        if (!string.IsNullOrEmpty(current) && !devices.Any(device => device.Name == current))
            devices.Add(new(current, current + " (현재 연결되지 않음)"));
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(device => device.Name == (current ?? ""));
        DeviceButton.IsEnabled = current is not null;
        if (current is null)
            Report(DeviceInfo, InfoBarSeverity.Informational, "사용자 설정 파일을 찾지 못했습니다",
                "선택한 폴더에서 osu!stable을 한 번 실행한 뒤 장치를 새로 고치세요.");
        else
            DeviceInfo.IsOpen = false;
    }

    private async void PickFolder(object sender, RoutedEventArgs e)
    {
        loading = true;
        UpdateControls();
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var selected = await picker.PickSingleFolderAsync();
            if (selected is null) return;
            if (!File.Exists(Path.Combine(selected.Path, "osu!.exe")) || !File.Exists(Path.Combine(selected.Path, "osu!.cfg")))
                throw new InvalidOperationException("osu!stable이 설치된 폴더를 선택하세요.");
            StableAudio.Save(selected.Path, saved);
            folder = selected.Path;
            await LoadDevicesAsync();
        }
        catch (Exception error) { Report(DeviceInfo, InfoBarSeverity.Error, "폴더를 설정하지 못했습니다", error.Message); }
        finally { loading = false; UpdateControls(); }
    }

    private void SaveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DeviceCombo.SelectedItem is not DeviceChoice device) return;
            StableAudio.SaveDevice(folder, device.Name);
            Report(DeviceInfo, InfoBarSeverity.Success, "게임 출력 장치를 저장했습니다", "다음에 osu!stable을 실행하면 적용됩니다.");
        }
        catch (Exception error) { Report(DeviceInfo, InfoBarSeverity.Error, "게임 출력 장치를 저장하지 못했습니다", error.Message); }
    }

    private async void DriverChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDrivers || loading || starting || capture is not null) return;
        loading = true;
        UpdateControls();
        try { await LoadDriverAsync(); }
        catch (Exception error) { Report(CaptureInfo, InfoBarSeverity.Error, "ASIO 드라이버를 열지 못했습니다", error.Message); }
        finally { loading = false; UpdateControls(); }
    }

    private async Task LoadDriverAsync()
    {
        details = null;
        PairCombo.ItemsSource = null;
        RateCombo.ItemsSource = null;
        if (DriverCombo.SelectedItem is not AsioDriver driver) return;
        details = await probe.InspectAsync(driver.Index);
        var channels = details.OutputChannels.ToDictionary(channel => channel.Index);
        var pairs = channels.Keys.Where(index => index % 2 == 0 && channels.ContainsKey(index + 1))
            .Order().Select(index => new StereoPair(index,
                $"{index + 1}+{index + 2} · {channels[index].Name} / {channels[index + 1].Name}")).ToArray();
        PairCombo.ItemsSource = pairs;
        PairCombo.SelectedItem = pairs.FirstOrDefault(pair => pair.FirstChannel == saved.FirstOutput) ?? pairs.FirstOrDefault();
        RateCombo.ItemsSource = details.SupportedRates;
        RateCombo.SelectedItem = details.SupportedRates.Contains(saved.SampleRate)
            ? saved.SampleRate : details.SupportedRates.FirstOrDefault();
        BufferBox.Value = ValidBuffer(saved.BufferFrames) ? saved.BufferFrames : details.PreferredBuffer;
        StatusText.Text = $"버퍼 {details.MinimumBuffer}–{details.MaximumBuffer} 프레임 · 권장 {details.PreferredBuffer}";
    }

    private bool ValidBuffer(int frames)
    {
        if (details is null || frames < details.MinimumBuffer || frames > details.MaximumBuffer) return false;
        return details.Granularity switch
        {
            -1 => frames > 0 && (frames & (frames - 1)) == 0,
            0 => frames == details.PreferredBuffer,
            > 0 => (frames - details.MinimumBuffer) % details.Granularity == 0,
            _ => true
        };
    }

    private async void ToggleCapture(object sender, RoutedEventArgs e)
    {
        if (capture is not null) { StopCapture(); return; }
        if (DriverCombo.SelectedItem is not AsioDriver driver || PairCombo.SelectedItem is not StereoPair pair
            || RateCombo.SelectedItem is not int rate) return;
        double buffer = BufferBox.Value;
        if (!double.IsFinite(buffer) || buffer != Math.Truncate(buffer) || !ValidBuffer((int)buffer))
        {
            Report(CaptureInfo, InfoBarSeverity.Warning, "버퍼 프레임을 확인하세요", "드라이버에서 지원하는 정수 값을 입력하세요.");
            return;
        }

        Process? worker = null;
        starting = true;
        UpdateControls();
        StatusText.Text = "ASIO에 연결하는 중";
        CaptureInfo.IsOpen = false;
        try
        {
            var route = new RouteSettings(driver.Name, pair.FirstChannel, rate, (int)buffer);
            StableAudio.Save(folder, route);
            saved = route;
            worker = StableAudio.Connect(folder, route);
            capture = worker;
            Task<string> stderr = worker.StandardError.ReadToEndAsync();
            string? ready = await worker.StandardOutput.ReadLineAsync();
            if (capture != worker) return;
            if (ready != "READY")
                throw new InvalidOperationException((await stderr).Trim());
            starting = false;
            StatusText.Text = "osu!stable 오디오를 ASIO로 보내는 중";
            UpdateControls();
            await worker.WaitForExitAsync();
            if (capture != worker) return;
            if (worker.ExitCode != 0)
                Report(CaptureInfo, InfoBarSeverity.Error, "ASIO 연결이 종료됐습니다", (await stderr).Trim());
        }
        catch (Exception error)
        {
            if (worker is null || capture == worker)
                Report(CaptureInfo, InfoBarSeverity.Error, "ASIO에 연결하지 못했습니다", error.Message);
        }
        finally
        {
            if (capture == worker) StopCapture();
        }
    }

    public void StopCapture()
    {
        Process? worker = capture;
        capture = null;
        starting = false;
        if (worker is not null)
        {
            try { if (!worker.HasExited) worker.Kill(); }
            catch (InvalidOperationException) { }
            finally { worker.Dispose(); }
        }
        StatusText.Text = "연결 대기 중";
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool editable = !loading && !starting && capture is null;
        FolderButton.IsEnabled = ReloadButton.IsEnabled = DriverCombo.IsEnabled = editable;
        PairCombo.IsEnabled = RateCombo.IsEnabled = BufferBox.IsEnabled = editable && details is not null;
        DeviceCombo.IsEnabled = !loading;
        ConnectButton.IsEnabled = !starting && !loading &&
            (capture is not null || (details?.Outputs >= 2 && details.SupportedRates.Count > 0));
        ConnectButton.Content = capture is null ? "ASIO 연결" : "연결 해제";
    }

    private static void Report(InfoBar bar, InfoBarSeverity severity, string title, string message)
    {
        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }

    private sealed record DeviceChoice(string Name, string Label);
}
