using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AnyCam.Desktop.ViewModels;

namespace AnyCam.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;
    private bool _disposed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        RestorePlacement();

        var engine = viewModel.Engine;
        Preview.SurfaceCreated += (hwnd, width, height) => engine.AttachPreview(hwnd, width, height);
        Preview.SurfaceResized += (width, height) => engine.ResizePreview(width, height);
        Preview.SurfaceDestroyed += engine.DetachPreview;
        Preview.SurfaceClicked += (x, y) => Dispatcher.BeginInvoke(() => viewModel.OnPreviewClicked(x, y));
        Preview.SurfaceWheel += delta => Dispatcher.BeginInvoke(() => viewModel.OnPreviewWheel(delta));

        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void RestorePlacement()
    {
        var settings = _viewModel.Settings;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop) &&
            settings.WindowLeft >= SystemParameters.VirtualScreenLeft &&
            settings.WindowTop >= SystemParameters.VirtualScreenTop &&
            settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var settings = _viewModel.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_disposed)
            return;

        // Shut down cleanly (finish recordings, release the camera) before the window goes away.
        e.Cancel = true;
        if (_closing)
            return; // already shutting down
        _closing = true;
        SavePlacement();
        IsEnabled = false;
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _disposed = true;
            // Close() can't be called from inside Closing (the dispose above may finish synchronously).
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void ConnectByIp_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            Text = _viewModel.Settings.LastManualHost ?? "",
            MinWidth = 280,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var connect = new Button { Content = "Connect", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(connect);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Phone IP address (shown in the AnyCam app on the iPhone):",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Connect by IP",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Background,
            Foreground = Foreground,
            ShowInTaskbar = false,
        };
        connect.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        if (dialog.ShowDialog() == true)
            _viewModel.ConnectToHost(input.Text);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F9 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _viewModel.ToggleRecordCommand.Execute(null);
            e.Handled = true;
        }
    }
}
