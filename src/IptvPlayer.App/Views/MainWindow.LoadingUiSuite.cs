#if PLAYBACK_DIAGNOSTICS
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using IptvPlayer.App.Controls;

namespace IptvPlayer.App.Views;

public partial class MainWindow
{
    private async Task RunLoadingUiSuiteAsync()
    {
        var checks = new List<object>();
        var watch = Stopwatch.StartNew();
        var output = Path.Combine(AppContext.BaseDirectory, "diagnostics", "stability-suite.json");
        try
        {
            // Exercise the same startup card while a real channel is selected.
            // The video remains owned by MainWindow and resumes visibility below.
            PlayerView.Visibility = Visibility.Hidden;
            StartupLoadingOverlay.BeginAnimation(OpacityProperty, null);
            StartupLoadingOverlay.Opacity = 1;
            StartupLoadingOverlay.Visibility = Visibility.Visible;
            ApplyStartupBlur(true);
            await Task.Delay(800);
            if (StartupBackdrop.Source is not BitmapSource { IsFrozen: true } bitmap
                || bitmap.PixelWidth > 1280 || StartupBackdrop.Effect is not BlurEffect { Radius: > 0 }
                || StartupLoadingOverlay.Background is not SolidColorBrush { Color.A: 0 })
                throw new InvalidOperationException("Startup backdrop is not a blurred shell with a translucent overlay");
            checks.Add(new { check = "blurred-startup-backdrop", bitmap.PixelWidth, bitmap.PixelHeight });
            Save("running", "startup-visible", null);
            if (Environment.GetCommandLineArgs().Contains("--loading-ui-inspect", StringComparer.Ordinal))
                await Task.Delay(20000);

            var uiThread = Environment.CurrentManagedThreadId;
            if (StartupLoadingBar.RenderSize != new Size(316, 4))
                throw new InvalidOperationException("Startup bar exceeded its card bounds");
            await StartupLoadingBar.ResetAnimationMetricsAsync();
            await Task.Delay(1250);
            var baseline = await StartupLoadingBar.ReadAnimationSnapshotAsync();
            await StartupLoadingBar.ResetAnimationMetricsAsync();
            var before = await StartupLoadingBar.ReadAnimationSnapshotAsync();
            var stalledSamples = new List<SmoothLoadingIndicator.LoadingAnimationSnapshot>();
            var sampling = Task.Run(async () =>
            {
                for (var i = 0; i < 24; i++)
                {
                    stalledSamples.Add(await StartupLoadingBar.ReadAnimationSnapshotAsync());
                    await Task.Delay(50);
                }
            });
            Thread.Sleep(1250); // Deliberate diagnostic-only window dispatcher blockage.
            await sampling;
            var after = await StartupLoadingBar.ReadAnimationSnapshotAsync();
            checks.Add(new { check = "startup-bar-during-1250ms-ui-stall", uiThread, baseline, before, after,
                frames = after.Frames - before.Frames, distinctPositions = stalledSamples.Select(s => Math.Round(s.Position)).Distinct().Count(),
                samples = stalledSamples.Select(s => new { s.Position, s.Frames, s.MaxFrameGapMs }) });
            if (!after.Active || after.ThreadId == uiThread || after.Frames - before.Frames < Math.Max(30, baseline.Frames * 0.85)
                || stalledSamples.Select(s => Math.Round(s.Position)).Distinct().Count() < 15
                || after.MaxFrameGapMs > 100)
                throw new InvalidOperationException("Loading animation stopped while the window dispatcher was blocked");

            StartupLoadingOverlay.Visibility = Visibility.Collapsed;
            ApplyStartupBlur(false);
            PlayerView.ClearValue(VisibilityProperty);
            await Task.Delay(300);
            if ((await StartupLoadingBar.ReadAnimationSnapshotAsync()).Active || StartupBackdrop.Source is not null)
                throw new InvalidOperationException("Dismissed startup indicator or backdrop stayed active");
            checks.Add(new { check = "startup-animation-and-backdrop-stopped" });

            // Native fullscreen changes must preserve the section binding itself,
            // including when fullscreen is entered from Movies or Series.
            using var originalMedia = _boundMediaPlayer?.Media;
            var originalChannel = _viewModel.SelectedChannel?.Id;
            for (var round = 0; round < 3; round++)
            {
                foreach (var section in new[] { "live", "movies", "series", "events", "live" })
                {
                    SelectSection(section);
                    await Task.Delay(700);
                    VerifyProgramVisibility(section);
                    if (section is "live" or "movies" or "series" or "events")
                    {
                        ToggleFullscreen();
                        await WaitForTransitionAsync();
                        ToggleFullscreen();
                        await WaitForTransitionAsync();
                        VerifyProgramVisibility(section);
                        if (!BindingOperations.IsDataBound(LiveProgramPanel, VisibilityProperty))
                            throw new InvalidOperationException("Fullscreen removed the Live TV section binding");
                    }
                    using var currentMedia = _boundMediaPlayer?.Media;
                    if (originalChannel != _viewModel.SelectedChannel?.Id
                        || currentMedia?.NativeReference != originalMedia?.NativeReference
                        || GetDiagnosticVideoRoot(PlayerView.Handle, 2) != new WindowInteropHelper(this).Handle)
                        throw new InvalidOperationException("Section navigation changed or detached live playback");
                    checks.Add(new { check = "section-epg-and-embedded-player", round, section });
                }
            }
            // Every loading surface uses the same indicator, with one shared STA.
            var indicatorOwners = new List<FrameworkElement>();
            indicatorOwners.AddRange(LoadingDescendants(this).OfType<SmoothLoadingIndicator>());
            if (_fullscreenHudWindow is not null)
                indicatorOwners.AddRange(LoadingDescendants(_fullscreenHudWindow).OfType<SmoothLoadingIndicator>());
            await Task.Delay(300);
            foreach (var indicator in indicatorOwners.Cast<SmoothLoadingIndicator>().Where(i => !i.IsVisible))
                if ((await indicator.ReadAnimationSnapshotAsync()).Active)
                    throw new InvalidOperationException("Hidden loading indicator stayed active");
            checks.Add(new { check = "all-hidden-indicators-stopped", count = indicatorOwners.Count(i => !i.IsVisible) });
            Save("completed", "finished", null);
        }
        catch (Exception e)
        {
            Save("failed", "failed", e.GetType().Name + ": " + e.Message);
        }

        void Save(string state, string stage, string? error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { state, stage, passed = state == "completed",
                error, liveOnly = true, elapsedSeconds = watch.Elapsed.TotalSeconds, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        void SelectSection(string section)
        {
            switch (section)
            {
                case "live": _viewModel.SelectLiveTvSectionCommand.Execute(null); break;
                case "movies": _viewModel.SelectMoviesSectionCommand.Execute(null); break;
                case "series": _viewModel.SelectSeriesSectionCommand.Execute(null); break;
                case "events": _viewModel.SelectEventsSectionCommand.Execute(null); break;
            }
        }
        void VerifyProgramVisibility(string section)
        {
            RootLayout.UpdateLayout();
            var visible = section is "live" or "events";
            if (LiveProgramPanel.IsVisible != visible || LiveEpgCard.IsVisible != visible || LiveChannelHeader.IsVisible != visible)
                throw new InvalidOperationException("Live TV information visibility is wrong in " + section);
        }
        async Task WaitForTransitionAsync()
        {
            await Task.Delay(300);
            var transition = Stopwatch.StartNew();
            while (_isFullscreenTransitioning && transition.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            if (_isFullscreenTransitioning) throw new InvalidOperationException("Fullscreen transition did not finish");
        }
    }

    private static IEnumerable<DependencyObject> LoadingDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in LoadingDescendants(child)) yield return descendant;
        }
    }
}
#endif
