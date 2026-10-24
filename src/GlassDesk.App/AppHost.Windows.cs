using System.Windows;
using GlassDesk.App.Startup;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;
using GlassDesk.Settings;

namespace GlassDesk.App;

/// <summary>
/// 启动台与设置窗口的按需构造。
/// </summary>
/// <remarks>
/// 这两个窗口在绝大多数会话里直到用户第一次点击之前都不会出现，
/// 却和程序坞一样要在启动时全额支付 XAML 解析、数据绑定与图片解码的代价。
/// 这里把它们改成首次用到时才构造：
/// <list type="bullet">
/// <item>关闭与图标等窗口级钩子在建窗时一次装好，语义与之前完全一致；</item>
/// <item>依赖宿主服务（应用库、交互控制器、性能协调器）的接线用 Wire* 方法完成，
/// 两个方向都能生效——窗口先建、控制器后建，或反过来。</item>
/// </list>
/// </remarks>
internal sealed partial class AppHost
{
    /// <summary>取得启动台窗口，必要时先构造。</summary>
    internal LauncherWindow EnsureLauncher()
    {
        if (_launcher is not null)
        {
            return _launcher;
        }

        var primaryDisplay = _primaryDisplaySession
            ?? throw new InvalidOperationException("Primary Display must be captured before creating GlassDesk windows.");

        var launcher = new LauncherWindow();
        launcher.BindPrimaryDisplay(primaryDisplay);
        launcher.ContainerAnchorProvider = () => _dock?.GetLauncherButtonPixelBounds();
        launcher.IsVisibleChanged += (_, e) => _dock?.SetLauncherContainerState(e.NewValue is true);
        launcher.InternalDragStarted += (_, _) => _dock?.RaiseAboveLauncherDuringDrag();
        launcher.InternalDragFinished += (_, _) => launcher.RaiseAboveDockAfterDrag();
        launcher.Icon = WindowIcon;
        launcher.Closing += OnWindowClosing;

        launcher.AttachServices(_services!, _loadReport?.LibraryStatus != LoadStatus.Unrecoverable);
        launcher.ApplySettings(_data!.Settings.Current.Launcher);

        _launcher = launcher;
        _startupTimeline.Mark("启动台窗口创建");

        // 性能协调器已经初始化过（首帧之后才建窗）时，补上它的可见性订阅；
        // 反过来的顺序由 InitializePerformanceCoordinator 负责。
        if (_performance is not null)
        {
            launcher.IsVisibleChanged += OnPerformanceWindowVisibilityChanged;
        }

        WireLauncherForInteraction(launcher);
        return launcher;
    }

    /// <summary>取得设置窗口，必要时先构造。</summary>
    internal SettingsWindow EnsureSettingsWindow()
    {
        if (_settings is not null)
        {
            return _settings;
        }

        var primaryDisplay = _primaryDisplaySession
            ?? throw new InvalidOperationException("Primary Display must be captured before creating GlassDesk windows.");

        var window = new SettingsWindow();
        window.BindPrimaryDisplay(primaryDisplay);
        window.Icon = WindowIcon;
        window.Closing += OnWindowClosing;

        window.AttachDataStore(_data!, _loadReport);
        window.DiagnosticsExportRequested += async (_, _) => await ExportDiagnosticsAsync();
        window.AttachStartupRegistration(new StartupRegistration(), Environment.ProcessPath ?? string.Empty);
        window.AttachInstallation(new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty));
        window.LauncherSettingsChanged += (_, settings) => _launcher?.ApplySettings(settings);

        _settings = window;
        _startupTimeline.Mark("设置窗口创建");

        if (_performance is not null)
        {
            window.PerformanceSettingsChanged += OnPerformanceSettingsChanged;
        }

        WireSettingsForUpdates(window);
        WireSettingsForInteraction(window);
        return window;
    }

    /// <summary>把更新检查的通知口接到设置窗口上。</summary>
    private void WireSettingsForUpdates(SettingsWindow window)
    {
        window.CheckForUpdatesRequested -= OnCheckForUpdatesRequested;
        window.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        window.ShowUpdateStatus(
            AppInfo.Version,
            $"当前版本 {AppInfo.FullLabel}。启动时会自动检查新版本，也可以手动检查。");
    }

    private async void OnCheckForUpdatesRequested(object? sender, EventArgs e) => await CheckUpdatesAsync(manual: true);

    /// <summary>把交互服务的入口接到启动台上；控制器还没建好时由它反问一次。</summary>
    private void WireLauncherForInteraction(LauncherWindow window)
    {
        window.NavigationAvailabilityChanged -= OnLauncherNavigationAvailabilityChanged;
        window.NavigationAvailabilityChanged += OnLauncherNavigationAvailabilityChanged;

        if (_interaction is not null)
        {
            _interaction.SetLauncherAvailability(window.IsVisible, window.CanNavigatePages);
        }
    }

    /// <summary>把交互服务的入口接到设置窗口上；控制器还没建好时由它反问一次。</summary>
    private void WireSettingsForInteraction(SettingsWindow window)
    {
        if (_interaction is null)
        {
            return;
        }

        window.InteractionSettingsApply = ApplyInteractionSettings;
        window.DesktopIconsRead = _interaction.ReadDesktopIcons;
        window.DesktopIconsSet = _interaction.SetDesktopIcons;

        // 这些事件可能会被重复接线（窗口先建/控制器先建两条路径），先退订再加。
        window.DataRestored -= OnSettingsDataRestored;
        window.DataRestored += OnSettingsDataRestored;
        window.UserDataDeletionRequested -= OnSettingsUserDataDeletionRequested;
        window.UserDataDeletionRequested += OnSettingsUserDataDeletionRequested;
        window.UninstallRequested -= OnSettingsUninstallRequested;
        window.UninstallRequested += OnSettingsUninstallRequested;
    }
}
