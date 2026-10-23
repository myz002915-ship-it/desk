using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.Core.Models;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;
public sealed partial class LiquidGlassSurface : FrameworkElement
{
    private void UpdateTransitionLayer(Size? layoutSize = null)
    {
        var size = layoutSize ?? RenderSize;
        var panel = _panelShape ?? new Rect(size);
        var radius = _panelShape is null ? (Window.GetWindow(this) as GlassWindow)?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var opticalMatches = _frame is not null && _sampledGlassPanelBounds == panel && _sampledGlassPanelRadius == radius;
        var enabled = (_transitionCaptureBounds is not null || !opticalMatches)
            && !_background.RequiresBasicBackground
            && _transitionBackdrop is not null && _transitionBackdropBounds.Contains(panel)
            && LiquidGlassDebugState.View == LiquidGlassDebugView.None
            && ThemeService.Current?.UseOpaquePanels != true
            && (ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass)
                is GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass;
        _transitionLayer.SetBackdrop(enabled ? _transitionBackdrop : null);
        _transitionLayer.Opacity = enabled ? 1 : 0;
        var frosted = ThemeService.Current?.Appearance.GlassMaterial == GlassMaterialMode.FrostedGlass;
        if (enabled) _transitionLayer.SetPanel(panel, _transitionBackdropBounds, radius, .6 + .4 * _transitionProgress, UseLaunchpadOptics, frosted);
        var liveReady = !_background.RequiresBasicBackground && (enabled || opticalMatches);
        var needsCompatibility = ThemeService.Current?.UseOpaquePanels != true
            && !BackdropRecovery.RequiresBasicRendering && LiquidGlassDebugState.View == LiquidGlassDebugView.None
            && (!liveReady || _handoffStarted != 0);
        if (needsCompatibility && size.Width > 0 && size.Height > 0)
        {
            PrepareCompatibleMaterial(size);
            var opacity = !liveReady || _handoffStarted == 0 ? 1
                : Math.Clamp(1 - Stopwatch.GetElapsedTime(_handoffStarted).TotalMilliseconds / 100, 0, 1);
            _compatibilityLayer.SetBackdrop(_compatibilityBackdrop);
            if (_compatibilityLayer.Clip is null || _compatibilityClipKey != (panel, radius))
            {
                _compatibilityClipKey = (panel, radius);
                _compatibilityLayer.Clip = _panelGeometry ?? (Window.GetWindow(this) is GlassWindow glass
                    ? glass.CreateGlassPanelGeometry(panel, radius) : SquircleGlassGeometry.Create(panel, radius));
            }
            _compatibilityLayer.SetPanel(panel, _compatibilityBounds, radius, .6 + .4 * _transitionProgress, UseLaunchpadOptics, frosted);
            _compatibilityLayer.Opacity = opacity;
            if (opacity == 0)
            {
                _handoffStarted = 0; _compatibilityBackdrop = null; _compatibilityStamp = null;
                _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
                _compatibilityLayer.SetBackdrop(null);
            }
        }
        else { _compatibilityLayer.Opacity = 0; _compatibilityLayer.SetBackdrop(null); }
    }

    private static void RenderMaterialBackdropInto(GlassMaterialMode material, byte[] source, byte[] pixels,
        byte[] scratch, int width, int height, Size size, bool dark, double strength, double clarity, bool launchpad)
    {
        if (material == GlassMaterialMode.FrostedGlass)
            AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(source, pixels, scratch, width, height, size, dark, strength, clarity);
        else
            AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source, pixels, scratch, width, height, size, dark, strength,
                backgroundClarity: clarity, launchpadOptics: launchpad);
    }

    private MaterialStamp ReadMaterialStamp()
    {
        var origin = new Point();
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_revealOrigin is { } preparedOrigin && Window.GetWindow(this) is { IsVisible: false }) origin = preparedOrigin;
        else if (PresentationSource.FromVisual(this) is not null) origin = PointToScreen(new Point());
        else if (Window.GetWindow(this) is { } owner && PresentationSource.FromVisual(owner) is not null)
        {
            // Before the first Show the surface is not connected yet, but its
            // hidden HWND already has final screen coordinates and monitor DPI.
            origin = owner.PointToScreen(new Point()); dpi = VisualTreeHelper.GetDpi(owner);
        }
        return new(origin, _transitionCaptureBounds ?? CaptureBoundsInWindow ?? new Rect(RenderSize),
            dpi, ThemeService.Current?.Appearance.GlassMaterial ?? GlassMaterialMode.AndroidLiquidGlass,
            ThemeService.Current?.IsDark != false, BackgroundClarity, UseLaunchpadOptics,
            LiquidGlassDebugState.SamplingParameters, LiquidGlassDebugState.WeightParameters, LiquidGlassDebugState.LodParameters,
            LiquidGlassDebugState.FinishingParameters, LiquidGlassDebugState.BrdfParameters,
            LiquidGlassDebugState.DispersionParameters, GlassDesk.UI.BackdropRecovery.Generation);
    }

    private void PrepareCompatibleMaterial(Size size)
    {
        var stamp = ReadMaterialStamp();
        var bounds = new Rect(size);
        if (_compatibilityBackdrop is not null && _compatibilityStamp == stamp && _compatibilityBounds == bounds) return;
        if (_compatibilityIsDesktopSnapshot) RevealMaterialInvalidationReason = $"origin {_compatibilityStamp?.Origin} => {stamp.Origin}; dpi {_compatibilityStamp?.Dpi.DpiScaleX} => {stamp.Dpi.DpiScaleX}; bounds {_compatibilityBounds} => {bounds}; capture {_compatibilityStamp?.CaptureBounds} => {stamp.CaptureBounds}";
        _compatibilityStamp = stamp; _compatibilityBounds = bounds;
        // A small, fully opaque ambient texture is immediately available. It contains
        // no screen pixels and enters the exact production lens, tint and blur path.
        const int width = 32, height = 24;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var variation = (byte)(12 * x / width + 14 * y / height);
            source[i] = (byte)((stamp.Dark ? 51 : 208) + variation);
            source[i + 1] = (byte)((stamp.Dark ? 43 : 205) + variation);
            source[i + 2] = (byte)((stamp.Dark ? 37 : 200) + variation);
            source[i + 3] = 255;
        }
        var filtered = new byte[source.Length]; var scratch = new byte[source.Length];
        RenderMaterialBackdropInto(stamp.Material, source, filtered, scratch, width, height,
            bounds.Size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
        var compatible = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
        compatible.Freeze(); _compatibilityBackdrop = compatible;
        _compatibilityIsDesktopSnapshot = false; _compatibilityScene = null;
        _handoffStarted = 0;
        // A static Windows wallpaper can differ completely from the composed scene
        // (including dynamic wallpapers). Do not flash it over the launchpad.
        if (UseLaunchpadOptics || IsBackdropActive is not null) return;
        if (_performanceSleeping || _wallpaperPreparationPending || PresentationSource.FromVisual(this) is null) return;
        PrepareWallpaperMaterial(stamp, bounds);
    }

    /// <summary>Prepare current, fully filtered glass before a hidden launcher becomes visible.</summary>
    public void PrepareRevealMaterial(PixelRect host, PixelRect panel, double dpi)
    {
        if (Window.GetWindow(this) is not { } revealOwner
            || revealOwner.IsVisible && IsBackdropActive?.Invoke() != false || ThemeService.Current?.UseOpaquePanels == true
            || BackdropRecovery.RequiresBasicRendering || host.Width <= 0 || host.Height <= 0) return;
        var size = new Size(host.Width * 96d / dpi, host.Height * 96d / dpi);
        _revealOrigin = new Point(host.X, host.Y);
        _revealHost = host;
        Measure(size); Arrange(new Rect(size));
        EnsureBackdropScene(revealing: true);
        using var primed = PrimeRevealCapture(host, dpi, out var primedRegion);
        if (_frame is not null || _transitionBackdrop is not null) return;
        if (_compatibilityIsDesktopSnapshot && _compatibilityScene?.Equals(_backdropScene) == true
            && _compatibilityStamp == ReadMaterialStamp()) return;
        var region = new PixelRect(Math.Max(host.X, panel.X), Math.Max(host.Y, panel.Y),
            Math.Min(host.Right, panel.Right) - Math.Max(host.X, panel.X),
            Math.Min(host.Bottom, panel.Bottom) - Math.Max(host.Y, panel.Y));
        if (region.Width <= 0 || region.Height <= 0) return;
        var scale = Math.Min(1, Math.Min(2048d / Math.Max(host.Width, host.Height),
            Math.Sqrt(320_000d / ((double)host.Width * host.Height))));
        var width = Math.Max(1, (int)Math.Floor(host.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(host.Height * scale));
        var captureWidth = Math.Max(1, (int)Math.Floor(region.Width * scale));
        var captureHeight = Math.Max(1, (int)Math.Floor(region.Height * scale));
        var scene = _backdropScene;
        var pixels = CaptureRevealBackdrop(region, captureWidth, captureHeight);
        var usedNative = false;
        // The existing prewarm owner already excludes every product window. A
        // launcher/morph can make GDI unsafe while this native frame is available;
        // consume it instead of dropping it and exposing an ambient first frame.
        if ((pixels is null || pixels.Length != captureWidth * captureHeight * 4 || IsSolidBlack(pixels))
            && !UseLaunchpadOptics && primed is { IsCaptured: true }
            && primed.Source is GlassBackdropSource.LiveDesktop or GlassBackdropSource.CompatibleScene
            && _background.LastCaptureTiming is { } timing
            && Stopwatch.GetTimestamp() - timing.SourceTicks is var age && age >= 0 && age <= Stopwatch.Frequency * 120 / 1000)
        {
            usedNative = true;
            pixels = new byte[captureWidth * captureHeight * 4];
            for (var y = 0; y < captureHeight; y++)
            {
                var sy = Math.Clamp((int)((region.Y + (y + .5) * region.Height / captureHeight - primedRegion.Y)
                    * primed.Height / primedRegion.Height), 0, primed.Height - 1);
                for (var x = 0; x < captureWidth; x++)
                {
                    var sx = Math.Clamp((int)((region.X + (x + .5) * region.Width / captureWidth - primedRegion.X)
                        * primed.Width / primedRegion.Width), 0, primed.Width - 1);
                    primed.Pixels.AsSpan((sy * primed.Width + sx) * 4, 4).CopyTo(pixels.AsSpan((y * captureWidth + x) * 4, 4));
                }
            }
        }
        if (pixels is null || pixels.Length != captureWidth * captureHeight * 4 || (!usedNative && IsSolidBlack(pixels))
            || scene is null || !scene.Equals(ReadBackdropScene())) return;
        // Extend only outside the destination panel. The Dock lies outside this
        // capture and cannot be burnt into the expanding or resting launchpad.
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((int)((host.Y + (y + .5) * host.Height / height - region.Y)
                * captureHeight / region.Height), 0, captureHeight - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((int)((host.X + (x + .5) * host.Width / width - region.X)
                    * captureWidth / region.Width), 0, captureWidth - 1);
                pixels.AsSpan((sy * captureWidth + sx) * 4, 4).CopyTo(source.AsSpan((y * width + x) * 4, 4));
            }
        }
        var stamp = ReadMaterialStamp();
        var filtered = new byte[source.Length]; var scratch = new byte[source.Length];
        RenderMaterialBackdropInto(stamp.Material, source, filtered, scratch, width, height,
            size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
        bitmap.Freeze();
        _wallpaperCancellation?.Cancel();
        _compatibilityBackdrop = bitmap; _compatibilityStamp = stamp; _compatibilityBounds = new Rect(size);
        _compatibilityScene = scene; _compatibilityIsDesktopSnapshot = true; _handoffStarted = 0;
        ++_preparedDesktopSnapshots; RevealMaterialInvalidationReason = null;
        UpdateTransitionLayer(size); InvalidateVisual();
    }

    private byte[]? CaptureHiddenDesktop(PixelRect region, int width, int height)
    {
        using var sampler = new GlassDesk.Platform.DesktopRegionSampler();
        var owner = Window.GetWindow(this);
        var emptyOwner = owner is { IsVisible: true } && IsBackdropActive?.Invoke() == false
            ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : 0;
        return sampler.TryCaptureBeforeReveal(region, width, height, emptyOwner, out var pixels) ? pixels : null;
    }

    private GlassBackgroundFrame? PrimeRevealCapture(PixelRect host, double dpi, out PixelRect region)
    {
        region = default;
        if (_performanceSleeping || Window.GetWindow(this) is not GlassWindow owner
            || owner.SurfaceMode != GlassSurfaceMode.OpticalGlass) return null;
        var scaleFactor = dpi / 96;
        var bounds = _transitionCaptureBounds ?? new Rect(0, 0, host.Width / scaleFactor, host.Height / scaleFactor);
        // Use the full fixed slide/morph capture bounds, not just the resting
        // panel. Otherwise the visible frame changes resolution/generation and
        // throws away the prewarmed snapshot on wide Docks.
        var left = (int)Math.Floor(host.X + (bounds.Left - 48) * scaleFactor);
        var top = (int)Math.Floor(host.Y + (bounds.Top - 48) * scaleFactor);
        var right = (int)Math.Ceiling(host.X + (bounds.Right + 48) * scaleFactor);
        var bottom = (int)Math.Ceiling(host.Y + (bounds.Bottom + 48) * scaleFactor);
        var requested = new PixelRect(left, top, right - left, bottom - top);
        var display = owner.PrimaryDisplaySession?.Display ?? DisplayService.Primary();
        if (display is null) return null;
        region = ClipRegion(requested, display.Bounds);
        if (region.Width <= 0 || region.Height <= 0) return null;
        var budget = UseLaunchpadOptics
            ? 320_000 + 680_000 * Math.Clamp((BackgroundClarity - .5) * 2, 0, 1) : 320_000;
        var scale = Math.Min(1, Math.Min(2048d / Math.Max(requested.Width, requested.Height),
            Math.Sqrt(budget / ((double)requested.Width * requested.Height))));
        // Start the existing isolated owner before GDI and filtering block the
        // Dispatcher. The first visible render uses this same crop and sample size.
        var pending = _background.Capture(region, Math.Max(1, (int)Math.Floor(region.Width * scale)),
            Math.Max(1, (int)Math.Floor(region.Height * scale)), _panelRadius, true);
        _revealCaptureUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        _timer.Interval = TimeSpan.FromMilliseconds(100); _timer.Start();
        return pending;
    }

    private void PrepareWallpaperMaterial(MaterialStamp stamp, Rect bounds)
    {
        _wallpaperPreparationPending = true;
        var cancellation = new CancellationTokenSource();
        _wallpaperCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _lifecycleGeneration;
        var scene = _backdropScene;
        var region = new Rect(stamp.Origin.X, stamp.Origin.Y,
            bounds.Width * stamp.Dpi.DpiScaleX, bounds.Height * stamp.Dpi.DpiScaleY);
        var scale = Math.Min(1, Math.Min(2048.0 / Math.Max(region.Width, region.Height),
            Math.Sqrt(320_000 / (region.Width * region.Height))));
        var width = Math.Max(1, (int)Math.Floor(region.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(region.Height * scale));
        var preparation = new Thread(() =>
        {
            BitmapSource? result = null;
            try
            {
                var wallpaper = new WallpaperBackdrop();
                if (!token.IsCancellationRequested) wallpaper.Update(region, width, height, 0);
                var pixels = wallpaper.Pixels;
                // A hidden black Windows wallpaper is common under dynamic wallpaper.
                // Never show that bitmap as a replacement for the visible background.
                if (!token.IsCancellationRequested && pixels is not null && !IsSolidBlack(pixels))
                {
                    var filtered = new byte[pixels.Length]; var scratch = new byte[pixels.Length];
                    RenderMaterialBackdropInto(stamp.Material, pixels, filtered, scratch, width, height,
                        bounds.Size, stamp.Dark, 1, stamp.Clarity, stamp.Launchpad);
                    result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, filtered, width * 4);
                    result.Freeze();
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException
                or ArgumentException or NotSupportedException)
            { Trace.WriteLine("Compatible first material: " + error.Message); }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) { cancellation.Dispose(); return; }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (ReferenceEquals(_wallpaperCancellation, cancellation)) _wallpaperCancellation = null;
                cancellation.Dispose();
                if (generation != _lifecycleGeneration) return;
                _wallpaperPreparationPending = false;
                if (token.IsCancellationRequested || _compatibilityIsDesktopSnapshot || _performanceSleeping || result is null || stamp != ReadMaterialStamp()
                    || scene is null || !scene.Equals(_backdropScene) || _handoffStarted != 0
                    || _frame is not null || _transitionBackdrop is not null) return;
                _compatibilityBackdrop = result; UpdateTransitionLayer(); InvalidateVisual();
            }));
        }) { IsBackground = true, Name = "GlassDesk first wallpaper material" };
        preparation.SetApartmentState(ApartmentState.STA); preparation.Start();
    }

    private static bool IsSolidBlack(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
        return true;
    }
}

