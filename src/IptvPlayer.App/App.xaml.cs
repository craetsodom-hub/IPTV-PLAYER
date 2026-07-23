using IptvPlayer.Application.DependencyInjection;
using IptvPlayer.Infrastructure.DependencyInjection;
using IptvPlayer.Player.Vlc.DependencyInjection;
using IptvPlayer.Presentation.DependencyInjection;
using IptvPlayer.App.Views;
using IptvPlayer.Presentation.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace IptvPlayer.App;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan UiExceptionDialogThrottle = TimeSpan.FromSeconds(20);

    private IHost? _host;
    private DateTimeOffset _lastUiExceptionDialogUtc = DateTimeOffset.MinValue;
    private bool _isUiExceptionDialogOpen;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        UiLocalization.Current.Initialize();

        RegisterGlobalExceptionHandlers();

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog((_, _, loggerConfiguration) =>
            {
                var logsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WhoseIPTV",
                    "logs");

                Directory.CreateDirectory(logsRoot);

                loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        Path.Combine(logsRoot, "iptv-player-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 10 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        shared: true);
            })
            .ConfigureServices((_, services) =>
            {
                services.AddApplicationServices();
                services.AddInfrastructureServices();
                services.AddPlayerVlcServices();
                services.AddPresentationServices();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }

        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        ShowThrottledUiExceptionDialog();

        e.Handled = true;
    }

    private void ShowThrottledUiExceptionDialog()
    {
        var now = DateTimeOffset.UtcNow;
        if (_isUiExceptionDialogOpen || now - _lastUiExceptionDialogUtc < UiExceptionDialogThrottle)
        {
            return;
        }

        _lastUiExceptionDialogUtc = now;
        _isUiExceptionDialogOpen = true;

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                MessageBox.Show(
                    MainWindow,
                    UiLocalization.Current.GetString("UnexpectedErrorDialog"),
                    "Whose IPTV",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _isUiExceptionDialogOpen = false;
            }
        });
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Error(exception, "Unhandled non-UI exception");
            return;
        }

        Log.Error("Unhandled non-UI exception object: {ExceptionObject}", e.ExceptionObject);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
