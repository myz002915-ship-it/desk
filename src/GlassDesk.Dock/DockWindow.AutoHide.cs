using System.Windows;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

// 自 DockWindow.xaml.cs 拆出的自动隐藏窗口定位/滑出/轮询 members（行为不变）。
public partial class DockWindow : GlassWindow
{
    public void PositionOnDisplay()
    {
        var handle = Handle;

        if (handle == 0)
        {
            return;
        }

        var display = CurrentPrimaryDisplay;
        if (display is null)
        {
            return;
        }

        // 用了 SizeToContent，最终尺寸要等布局跑完才知道。
        var widthDips = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var heightDips = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        // 极端缩放下取整可能得 0；窗口至少要有 1 物理像素。
        var widthPixels = Math.Max(1, display.ToPixels(widthDips));
        var heightPixels = Math.Max(1, display.ToPixels(heightDips));

        _restingRect = DockPlacementCalculator.Calculate(
            display.WorkArea,
            Settings.Placement,
            Settings.OffsetX,
            Settings.OffsetY,
            widthPixels,
            heightPixels,
            display.Dpi);

        if (PrimaryDisplaySession is { IsAvailable: true } session)
            _restingRect = session.ClampWindowToWorkArea(_restingRect);

        _fullyHiddenOffsetPixels = SlideOutDistancePixels(_restingRect, display.Bounds, Settings.Placement);

        ApplySlide(_restingRect, _slideOffsetPixels);
    }

    /// <summary>保持 HWND 在主屏工作区内，只在窗口剪裁范围内滑动内容与共享面板几何。</summary>
    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        // HWND equality describes layout, not the pixels painted while hidden.
        // Retire old glass only once the entire panel is clipped, so every reveal
        // prepares current content without disturbing the visible hide animation.
        if (concealed && !_panelWasConcealed) GlassSurface.RetireConcealedMaterial();
        _panelWasConcealed = concealed;
        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;
        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable) return;
            var fixedWindow = session.ClampWindowToWorkArea(restingRect);
            if (DisplayService.GetWindowPixelBounds(Handle) != fixedWindow)
                _ = DisplayService.MoveWindowToPixelRect(Handle, fixedWindow);
            var scale = Math.Max(.01, session.Display.ScaleFactor);
            var visualOffsetDip = slidePixels / scale;
            DockSlideTransform.Y = visualOffsetDip;
            _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                // Match the launcher: a fixed desktop texture with a moving GPU lens.
                // Only Content translates. New capture frames keep the same coordinates
                // throughout the slide and therefore remain consumable during motion.
                var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
                GlassSurface.SetPanelShape(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
                GlassSurface.SetContainerTransitionBounds(
                    new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                        ActualHeight + Math.Abs(hiddenOffsetDip)));
                SetSharedPanelClip(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
            }
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01
                    ? null
                    : _slidePanelBoundsDip,
                PanelCornerRadius.TopLeft, shadowStrength);
            return;
        }

        // Unbound design/test windows keep their legacy placement behavior. AppHost binds
        // every production GlassWindow to one immutable PrimaryDisplaySession.
        DockSlideTransform.Y = 0;
        SetTransitionShadowShape(null, 0);
        var rect = restingRect with { Y = restingRect.Y + slidePixels };
        _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
    }

    /// <summary>内容滑出方向：底部 Dock 向下 +1，顶部 Dock 向上 −1。</summary>
    private int SlideOutDirection()
        => Settings.Placement == DockPlacement.TopCenter ? -1 : 1;

    /// <summary>内容完全隐藏所需的物理像素偏移，窗口 HWND 本身始终留在主屏内。</summary>
    private static int SlideOutDistancePixels(PixelRect restingRect, PixelRect displayBounds, DockPlacement placement)
    {
        var distanceToDisplayEdge = placement == DockPlacement.TopCenter
            ? restingRect.Bottom - displayBounds.Y
            : displayBounds.Bottom - restingRect.Y;

        // Dock 靠工作区边缘停靠，工作区与显示器边缘之间可能有任务栏；再多移两像素，
        // 避免物理像素取整或窗口表面边界留下细线。
        return Math.Max(0, distanceToDisplayEdge) + 2;
    }

    // ---- 自动隐藏（D-19） ----

    /// <summary>按当前设置重建状态机。设置可随时改，状态机不可变所以整体换。</summary>
    private void RebuildAutoHide()
    {
        var settings = Settings;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);

        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;

        if (_frameTimer is null)
        {
            // Capture completion/icon work can keep Normal queued while the panel
            // is concealed. Poll/slide admission must still advance to its first
            // visible pixel; settled idle remains 100 ms and game sleep stops it.
            _frameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = FrameInterval,
            };
            _frameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        _frameTimer.IsEnabled = !_performanceSleepRequested;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// 公共帧：探测指针 → 状态机 → 滑动插值。
    /// </summary>
    /// <remarks>
    /// 全部判定用<strong>缓存的静止矩形</strong>与显示枚举，不碰 WPF 可视树；
    /// 16 ms 一次的成本只有一次光标查询与少量算术。
    /// </remarks>
    private void OnAutoHideFrame()
    {
        if (_performanceSleepRequested) { AdvancePerformanceSleep(); return; }
        if (_autoHide is null || _services is null || _restingRect.Width == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var suppressed = fullscreen || covered;
        var pointerOverDock = cursor is { } currentPoint
            && Inflate(GetVisibleDockRect(), 2).Contains(currentPoint.X, currentPoint.Y);

        var pointerOverDockPosition = cursor is { } position
            && Inflate(_restingRect, 2).Contains(position.X, position.Y);
        UpdateAutoHideState(now, fullscreen, covered, pointerOverDockPosition, pointerOverDock);

        // 滑动插值：向目标滑出量靠近。
        var target = _autoHide.State == AutoHideState.Hidden
            ? (double)_fullyHiddenOffsetPixels
            : 0.0;

        // Admit the reveal intent before preparation starts the native owner.
        // Waiting for optical visibility lets a foreground-policy tick put that
        // new owner back to sleep while the panel is still crossing the edge.
        NotifyPerformanceSurfaceVisibility();

        // Prepare a complete material before the first pixels slide back into view.
        if (target == 0 && _backgroundCaptureSleeping) SetBackgroundCaptureSleeping(false);
        else if (target == 0) PrepareConcealedReveal();
        if (target == 0 && !CanStartPreparedReveal(System.Diagnostics.Stopwatch.GetTimestamp()))
        {
            if (_frameTimer is not null) _frameTimer.Interval = FrameInterval;
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (target != 0) _revealPreparationStarted = 0;

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect,target);
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.25;

            if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
            {
                _slideOffsetPixels = target;
            }

            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        if (_frameTimer is not null)
            _frameTimer.Interval = !_backgroundCaptureSleeping && (suppressed || _autoHide.Suppressed)
                || Math.Abs(_slideOffsetPixels - target) > 0.5
                ? FrameInterval
                : TimeSpan.FromMilliseconds(100);
        NotifyPerformanceSurfaceVisibility();
    }

    private bool CanStartPreparedReveal(long now)
    {
        if (_restingRect.Height <= 0 || !_panelWasConcealed
            || GlassSurface.IsCurrentRevealMaterialReady)
        { _revealPreparationStarted = 0; return true; }
        if (_revealPreparationStarted == 0) _revealPreparationStarted = now;
        // Readiness, not a fixed settling delay, admits the first slide pixel.
        // Sustained capture failure must still expose the readable fallback.
        return now - _revealPreparationStarted >= System.Diagnostics.Stopwatch.Frequency * 3;
    }

    private void UpdateAutoHideState(DateTime now, bool fullscreen, bool covered,
        bool pointerOverDockPosition, bool pointerOverVisibleDock)
    {
        if (_autoHide is null) return;
        if (_launcherContainerActive)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (_autoHide.Suppressed || _tileStrip.IsDragging)
        {
            _edgeRevealedDuringSuppression = fullscreen || covered;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen || covered)
        {
            if (_revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverDockPosition, _revealLocked)
                    && _autoHide.TryReveal(false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverVisibleDock) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
                _autoHide.ForceHide(now);
        }
        else if (Settings.AutoHide)
        {
            _edgeRevealedDuringSuppression = false;
            if (pointerOverDockPosition || pointerOverVisibleDock)
                _autoHide.PointerEnter(now);
            else
            {
                _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
        }
        else
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }

        if (_autoHide.State == AutoHideState.Hidden)
            _edgeRevealedDuringSuppression = false;
    }

    private PixelRect GetVisibleDockRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        if (PrimaryDisplaySession is null) return translated;
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>切换本次运行期间的遮挡唤出锁；不影响未被覆盖的 Dock。</summary>
    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        if (_performanceSleepRequested) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.IsEnabled = true;
        }
        OnAutoHideFrame();
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);
}
