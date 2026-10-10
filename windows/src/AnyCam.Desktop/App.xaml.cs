using System.IO;
using System.Windows;
using AnyCam.Desktop.Services;
using AnyCam.Desktop.ViewModels;

namespace AnyCam.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\AnyCam.Desktop", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("AnyCam is already running.", "AnyCam", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show($"Something went wrong:\n\n{args.Exception.Message}\n\nDetails were saved to {CrashLogPath}.",
                "AnyCam", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                LogCrash(exception);
        };

        base.OnStartup(e);

        var settings = AppSettings.Load();
        var window = new MainWindow(new MainViewModel(settings));
        MainWindow = window;
        window.Show();
    }

    private static string CrashLogPath => Path.Combine(AppSettings.Folder, "crash.log");

    private static void LogCrash(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {exception}\n\n");
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
