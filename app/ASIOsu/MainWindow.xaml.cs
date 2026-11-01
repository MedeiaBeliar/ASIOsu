using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ASIOsu.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;
using System.Diagnostics;

namespace ASIOsu;

public sealed partial class MainWindow : Window
{
    private readonly InstallationManager installer = new();
    private readonly ProbeClient probe = new();
    private readonly ReleaseUpdater updater;
    private GameLocation game = GameDiscovery.Find();
    private InstallSnapshot snapshot = new(InstallState.Missing, "준비 중", false, false);
    private AsioDetails? details;
    private bool activated;
    private bool busy;
    private bool checkingUpdate;
    private ReleaseInfo? availableRelease;
    private readonly string? updatedTag;

    public MainWindow()
    {
        updater = new ReleaseUpdater(typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0));
        InitializeComponent();
        string[] arguments = Environment.GetCommandLineArgs();
        updatedTag = ArgumentValue(arguments, "--updated");
        UpdateState.Text = $"현재 v{updater.CurrentText}";
        if (arguments.Contains("--route", StringComparer.OrdinalIgnoreCase)) Navigation.SelectedItem = RouteItem;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        appWindow.Resize(new SizeInt32(1240, 820));
        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (activated) return;
        activated = true;
        await RefreshAsync();
        await LoadDriversAsync();
        if (!string.IsNullOrWhiteSpace(updatedTag)) ShowOverview(InfoBarSeverity.Success, "업데이트 완료", $"ASIOsu {updatedTag} 설치를 완료했습니다.");
        await CheckUpdateAsync(false);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool overview = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() != "route";
        OverviewPage.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        RoutePage.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAsync()
    {
        snapshot = installer.Inspect(game);
        GamePathText.Text = SidebarPath.Text = game.Directory;
        BridgeState.Text = snapshot.Summary;
        StatusSubtitle.Text = snapshot.Summary;
        StatusBadge.Text = snapshot.State switch
        {
            InstallState.Installed => "ASIO",
            InstallState.Original => "원본",
            InstallState.Legacy => "이전 프록시",
            InstallState.Damaged => "확인 필요",
            _ => "폴더 필요"
        };
        InstallButton.IsEnabled = snapshot.CanInstall && !busy;
        RestoreButton.IsEnabled = snapshot.CanRestore && !busy;
        RestoreState.Text = snapshot.CanRestore ? "검증된 원본을 사용할 수 있습니다." : "복구 지점 없음";
        RouteSettings route = installer.LoadRoute(game);
        RouteSummary.Text = string.IsNullOrWhiteSpace(route.Driver) ? "설정 필요" : $"{route.Driver} · {route.SampleRate / 1000.0:0.#} kHz · {route.BufferFrames} frames";
        TitleStatus.Text = snapshot.State switch
        {
            InstallState.Installed => "ASIO 브리지 사용 중",
            InstallState.Legacy => "이전 프록시 사용 중",
            InstallState.Original => "원본 오디오 사용 중",
            _ => snapshot.Summary
        };
        await Task.CompletedTask;
    }

    private async Task LoadDriversAsync()
    {
        try
        {
            IReadOnlyList<AsioDriver> drivers = await probe.ListAsync();
            DriverCombo.ItemsSource = drivers;
            RouteSettings saved = installer.LoadRoute(game);
            DriverCombo.SelectedItem = drivers.FirstOrDefault(driver => driver.Name.Equals(saved.Driver, StringComparison.OrdinalIgnoreCase));
            if (DriverCombo.SelectedItem is null && drivers.Count == 1) DriverCombo.SelectedIndex = 0;
            RateCombo.SelectedItem = RateCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == saved.SampleRate.ToString());
            if (RateCombo.SelectedIndex < 0) RateCombo.SelectedIndex = 1;
            BufferBox.Value = saved.BufferFrames;
        }
        catch (Exception exception) { ShowRoute(InfoBarSeverity.Error, "ASIO 드라이버를 읽지 못했습니다", exception.Message); }
    }

    private async void DriverCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        details = null;
        PairCombo.ItemsSource = null;
        DriverRangeText.Text = "채널 읽기를 눌러 출력 정보를 확인하세요.";
        await Task.CompletedTask;
    }

    private async void ReadDriverButton_Click(object sender, RoutedEventArgs e) => await ReadDriverAsync(true);

    private async Task<bool> ReadDriverAsync(bool report)
    {
        if (DriverCombo.SelectedItem is not AsioDriver driver)
        {
            if (report) ShowRoute(InfoBarSeverity.Warning, "드라이버를 선택하세요", "사용할 ASIO 드라이버가 필요합니다.");
            return false;
        }
        try
        {
            SetBusy(true, "ASIO 드라이버를 읽는 중");
            details = await probe.InspectAsync(driver.Index);
            var pairs = new List<StereoPair>();
            for (int index = 0; index + 1 < details.OutputChannels.Count; index += 2)
            {
                AsioChannel left = details.OutputChannels[index];
                AsioChannel right = details.OutputChannels[index + 1];
                pairs.Add(new(left.Index, $"출력 {left.Index + 1}+{right.Index + 1}: {left.Name} / {right.Name}"));
            }
            PairCombo.ItemsSource = pairs;
            RouteSettings saved = installer.LoadRoute(game);
            PairCombo.SelectedItem = pairs.FirstOrDefault(pair => pair.FirstChannel == saved.FirstOutput) ?? pairs.FirstOrDefault();
            DriverRangeText.Text = $"{details.MinimumBuffer}–{details.MaximumBuffer} frames · 권장 {details.PreferredBuffer} · 지원 {string.Join(", ", details.SupportedRates)} Hz · 출력 {details.Outputs}채널";
            if (report) ShowRoute(InfoBarSeverity.Success, "채널을 읽었습니다", details.Driver);
            return true;
        }
        catch (Exception exception)
        {
            if (report) ShowRoute(InfoBarSeverity.Error, "드라이버를 열지 못했습니다", exception.Message);
            return false;
        }
        finally { SetBusy(false); }
    }

    private RouteSettings CurrentRoute()
    {
        if (DriverCombo.SelectedItem is not AsioDriver driver) throw new InvalidOperationException("ASIO 드라이버를 선택하세요.");
        if (PairCombo.SelectedItem is not StereoPair pair) throw new InvalidOperationException("출력 쌍을 선택하세요.");
        if (!int.TryParse((RateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int rate)) throw new InvalidOperationException("샘플레이트를 선택하세요.");
        int frames = checked((int)BufferBox.Value);
        if (details is not null && (frames < details.MinimumBuffer || frames > details.MaximumBuffer))
            throw new InvalidOperationException($"버퍼는 {details.MinimumBuffer}–{details.MaximumBuffer} 범위여야 합니다.");
        if (details is not null && !details.SupportedRates.Contains(rate))
            throw new InvalidOperationException($"드라이버가 {rate} Hz를 지원하지 않습니다.");
        if (details is not null && details.Granularity == -1 && (frames & (frames - 1)) != 0)
            throw new InvalidOperationException("이 드라이버의 버퍼 프레임은 2의 거듭제곱이어야 합니다.");
        if (details is not null && details.Granularity == 0 && frames != details.PreferredBuffer)
            throw new InvalidOperationException($"이 드라이버는 {details.PreferredBuffer} frames만 지원합니다.");
        if (details is not null && details.Granularity > 0 && (frames - details.MinimumBuffer) % details.Granularity != 0)
            throw new InvalidOperationException($"버퍼 프레임은 {details.MinimumBuffer}부터 {details.Granularity} 단위여야 합니다.");
        return new(driver.Name, pair.FirstChannel, rate, frames);
    }

    private async void SaveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (details is null && !await ReadDriverAsync(false)) throw new InvalidOperationException("먼저 드라이버 채널을 읽으세요.");
            RouteSettings route = CurrentRoute();
            installer.WriteRoute(game, route, snapshot.State == InstallState.Installed);
            ShowRoute(InfoBarSeverity.Success, "저장했습니다", snapshot.State == InstallState.Installed ? "osu!lazer를 다시 시작하면 적용됩니다." : "설치할 때 이 설정이 적용됩니다.");
            await RefreshAsync();
        }
        catch (Exception exception) { ShowRoute(InfoBarSeverity.Error, "설정을 저장하지 못했습니다", exception.Message); }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "호환성을 검사하는 중");
            CompatibilityState.Text = installer.CheckCompatibility(game);
            ShowOverview(InfoBarSeverity.Success, "호환성 검사 완료", CompatibilityState.Text);
        }
        catch (Exception exception) { ShowOverview(InfoBarSeverity.Error, "호환성 검사 실패", exception.Message); }
        finally { SetBusy(false); await RefreshAsync(); }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (details is null && !await ReadDriverAsync(false))
            {
                Navigation.SelectedItem = RouteItem;
                throw new InvalidOperationException("오디오 경로에서 드라이버 채널을 읽고 출력 쌍을 선택하세요.");
            }
            RouteSettings route = CurrentRoute();
            SetBusy(true, "ASIO 브리지를 설치하는 중");
            await installer.InstallAsync(game, route);
            ShowOverview(InfoBarSeverity.Success, "설치 완료", "원본 BASS는 유지되고 전체 믹서가 ASIO로 전달됩니다. osu!lazer를 시작하세요.");
        }
        catch (Exception exception) { ShowOverview(InfoBarSeverity.Error, "설치하지 못했습니다", exception.Message); }
        finally { SetBusy(false); await RefreshAsync(); }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "원본 파일을 복구하는 중");
            await installer.RestoreAsync(game);
            ShowOverview(InfoBarSeverity.Success, "복구 완료", "검증된 원본 BASS 오디오 파일로 돌아갔습니다.");
        }
        catch (Exception exception) { ShowOverview(InfoBarSeverity.Error, "복구하지 못했습니다", exception.Message); }
        finally { SetBusy(false); await RefreshAsync(); }
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        game = GameDiscovery.Find(folder.Path);
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        await LoadDriversAsync();
        await CheckUpdateAsync(false);
    }

    private async Task CheckUpdateAsync(bool report)
    {
        checkingUpdate = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "확인 중";
        UpdateState.Text = $"현재 v{updater.CurrentText} · 확인 중";
        try
        {
            availableRelease = await updater.CheckAsync();
            if (availableRelease is null)
            {
                UpdateState.Text = $"최신 버전 · v{updater.CurrentText}";
                UpdateButton.Content = "다시 확인";
                if (report) ShowOverview(InfoBarSeverity.Success, "최신 버전입니다", $"현재 ASIOsu v{updater.CurrentText}을 사용 중입니다.");
            }
            else
            {
                UpdateState.Text = $"{availableRelease.Tag} 사용 가능";
                UpdateButton.Content = updater.CanInstall ? "다운로드 및 설치" : "릴리스 열기";
            }
        }
        catch (Exception exception)
        {
            availableRelease = null;
            UpdateState.Text = "업데이트 확인 실패";
            UpdateButton.Content = "다시 시도";
            if (report) ShowOverview(InfoBarSeverity.Error, "업데이트 확인 실패", exception.Message);
        }
        finally
        {
            checkingUpdate = false;
            UpdateButton.IsEnabled = !busy;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (availableRelease is null) { await CheckUpdateAsync(true); return; }
        if (!updater.CanInstall)
        {
            Process.Start(new ProcessStartInfo(availableRelease.PageUrl.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return;
        }
        bool started = false;
        try
        {
            SetBusy(true, "업데이트를 준비하는 중");
            var progress = new Progress<ReleaseProgress>(value =>
            {
                string percent = value.Percent is int number ? $" · {number}%" : "";
                UpdateState.Text = value.Text + percent;
                TitleStatus.Text = value.Text;
            });
            PreparedRelease prepared = await updater.DownloadAsync(availableRelease, progress);
            UpdateState.Text = "검증 완료 · 다시 시작하는 중";
            updater.Apply(prepared);
            started = true;
            Close();
        }
        catch (Exception exception)
        {
            UpdateState.Text = $"{availableRelease.Tag} 설치 실패";
            UpdateButton.Content = "다시 시도";
            ShowOverview(InfoBarSeverity.Error, "업데이트 설치 실패", exception.Message);
        }
        finally { if (!started) SetBusy(false); }
    }

    private void SetBusy(bool value, string? message = null)
    {
        busy = value;
        InstallButton.IsEnabled = !value && snapshot.CanInstall;
        RestoreButton.IsEnabled = !value && snapshot.CanRestore;
        CheckButton.IsEnabled = !value;
        SaveRouteButton.IsEnabled = !value;
        ReadDriverButton.IsEnabled = !value;
        UpdateButton.IsEnabled = !value && !checkingUpdate;
        if (!string.IsNullOrWhiteSpace(message)) TitleStatus.Text = message;
    }

    private void ShowOverview(InfoBarSeverity severity, string title, string message)
    {
        OverviewInfo.Severity = severity;
        OverviewInfo.Title = title;
        OverviewInfo.Message = message;
        OverviewInfo.IsOpen = true;
    }

    private void ShowRoute(InfoBarSeverity severity, string title, string message)
    {
        RouteInfo.Severity = severity;
        RouteInfo.Title = title;
        RouteInfo.Message = message;
        RouteInfo.IsOpen = true;
    }

    private static string? ArgumentValue(string[] arguments, string name)
    {
        int index = Array.FindIndex(arguments, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
}

