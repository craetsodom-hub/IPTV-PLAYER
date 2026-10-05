using IptvPlayer.App.Themes;
using IptvPlayer.Application.DependencyInjection;
using IptvPlayer.Application.Services;
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
#if PLAYBACK_DIAGNOSTICS
    internal IServiceProvider DiagnosticServices => _host!.Services;
#endif
    private DateTimeOffset _lastUiExceptionDialogUtc = DateTimeOffset.MinValue;
    private bool _isUiExceptionDialogOpen;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
#if PLAYBACK_DIAGNOSTICS
        var profileArgument = Array.IndexOf(e.Args, "--playback-test-profile");
        if (profileArgument >= 0 && profileArgument + 1 < e.Args.Length)
        {
            var testRoot = Path.GetFullPath(e.Args[profileArgument + 1]);
            Directory.CreateDirectory(testRoot);
            Environment.SetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT", testRoot);
        }
        if (Environment.GetCommandLineArgs().Contains("--software-ui-rendering", StringComparer.Ordinal))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
#endif
        base.OnStartup(e);

        UiLocalization.Current.Initialize();
        ThemeManager.Current.Initialize(Resources);
        DesignManager.Current.Initialize(Resources);

        RegisterGlobalExceptionHandlers();

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog((_, _, loggerConfiguration) =>
            {
                var logsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WhoseIPTV",
                    "logs");
#if PLAYBACK_DIAGNOSTICS
                logsRoot = Path.Combine(AppContext.BaseDirectory, "diagnostics");
                loggerConfiguration.Filter.ByIncludingOnly(log =>
                    log.MessageTemplate.Text.StartsWith("Playback frame diagnostics", StringComparison.Ordinal)
                    || log.MessageTemplate.Text.StartsWith("Playback native diagnostics", StringComparison.Ordinal)
                    || log.MessageTemplate.Text.StartsWith("Playback diagnostics:", StringComparison.Ordinal)
                    || log.MessageTemplate.Text.StartsWith("Playback test", StringComparison.Ordinal));
#endif

                Directory.CreateDirectory(logsRoot);

                loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        Path.Combine(logsRoot, "iptv-player-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
#if PLAYBACK_DIAGNOSTICS
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}",
#endif
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
        // Show the loading UI before waiting for the catalog. Shell initialization
        // already reads sources and clears loading when this is a fresh install.
        window.SetStartupLoadingState(true);
        window.WindowState = WindowState.Maximized;
        window.Show();
        window.Activate();
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
