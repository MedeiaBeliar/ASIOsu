using Microsoft.UI.Xaml;
using ASIOsu.Services;

namespace ASIOsu;

public partial class App : Application
{
    private Window? window;

    public App()
    {
        ReleaseUpdater.CleanupHosts();
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASIOsu");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.Now:O}\r\n{args.Exception}\r\n\r\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}

