using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Platform.Installation;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private int _maintenanceStarted;

    /// <summary>
    /// 把不参与首帧的启动工作排到消息循环空闲时执行。
    /// </summary>
    /// <remarks>
    /// <see cref="DispatcherPriority.ApplicationIdle"/> 低于 Render，因此这些工作一定在
    /// 程序坞第一次提交渲染之后才开始；它们都只碰已经存在的数据与服务，不创建窗口句柄。
    /// </remarks>
    private void ScheduleDeferredStartupWork()
    {
        if (_application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunDeferredStartupWork));
    }

    /// <summary>
    /// 首帧之后的启动工作：运行状态首次枚举、目标失效复查、启动台预热。
    /// </summary>
    /// <remarks>
    /// 这三件事以前都在 <c>_dock.Show()</c> 之前同步跑完，于是「第一次画出程序坞」要等
    /// 全库文件系统探测、全窗口枚举和一个不可见窗口的 XAML 解析。移到首帧之后，
    /// 结果到达时由各自的通知路径刷新界面（状态点只改可见性，失效标记走库变更刷新）。
    /// </remarks>
    private void RunDeferredStartupWork()
    {
        if (_disposed || _shuttingDown || _services is null)
        {
            return;
        }

        _startupTimeline.Mark("首帧之后");

        // 1) 运行状态：首次全量枚举 + 事件钩子。首帧里状态点暂时全灭，
        //    Changed 到达后由 Dock 自行补上，不会重建磁贴。
        _services.Running.Start();
        _startupTimeline.Mark("运行状态跟踪就绪");

        // 2) 目标失效复查：逐条 File.Exists/Directory.Exists，条目指向网络路径或
        //    已拔掉的移动盘时会阻塞到卷超时，因此必须在首帧之后。
        _missingReport = _services.Maintenance.RefreshMissingTargets();
        _startupTimeline.Mark("目标失效复查完成");

        // 3) 预热启动台：第一次打开时不必再付 XAML 解析与控件树构建的代价。
        _ = EnsureLauncher();
        _startupTimeline.Mark("启动台预热完成");

        if (_data is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] 启动后续任务",
            $"  缓存图标：{_services.Icons.FileCount} 个",
        };

        if (_missingReport is not null)
        {
            lines.Add(
                $"  目标失效：{_missingReport.Missing} 条（检查 {_missingReport.Checked} 条，"
                + $"新发现 {_missingReport.NewlyBroken.Count} 条，恢复 {_missingReport.Recovered.Count} 条）");
        }

        lines.AddRange(_startupTimeline.Describe(_timelineLogged));
        _timelineLogged = _startupTimeline.Count;
        AppendToLog(lines);
    }

    private void OnSettingsUserDataDeletionRequested(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) != 0)
        {
            return;
        }

        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        UserDataMaintenanceSequence.Execute(
            closeCommandDispatch: () =>
            {
                _deferredCommandGate.Close();
                _shuttingDown = true;
            },
            prepareSettings: () => _settings?.PrepareForUserDataMaintenance(),
            stopInteractions: DisposeInteractionController,
            stopTray: () =>
            {
                _tray?.Dispose();
                _tray = null;
            },
            stopServices: () =>
            {
                _services?.Dispose();
                _services = null;
            },
            closeMessageWindow: () =>
            {
                _messageWindow?.Dispose();
                _messageWindow = null;
            },
            releaseDataLease: () =>
            {
                _userDataLease?.Dispose();
                _userDataLease = null;
            },
            deleteData: () => UserDataDeletionService.ForCurrentUser(installDirectory).DeleteAll(confirmed: true),
            reportStopFailure: ex => MessageBox.Show(
                $"未能安全停止 GlassDesk 的数据写入，因此没有删除数据。请重新启动后重试。\n\n{ex.Message}",
                "GlassDesk 数据未删除",
                MessageBoxButton.OK,
                MessageBoxImage.Error),
            presentResult: result =>
            {
                var (message, icon) = FormatDeletionResult(result);
                MessageBox.Show(message, "GlassDesk 数据维护", MessageBoxButton.OK, icon);
            },
            shutdown: _application.Shutdown);
    }

    private void OnSettingsUninstallRequested(object? sender, EventArgs e)
    {
        if (_shuttingDown || Volatile.Read(ref _maintenanceStarted) != 0) return;

        var info = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
        if (info.State != InstallationState.VerifiedInstalled || string.IsNullOrWhiteSpace(info.UninstallerPath))
        {
            _settings?.AttachInstallation(info);
            return;
        }

        try
        {
            // 仅启动已核验的可执行文件路径；不传递或执行注册表中的 UninstallString 参数。
            using var process = Process.Start(new ProcessStartInfo(info.UninstallerPath) { UseShellExecute = true });
            if (process is null)
            {
                _settings?.ShowUninstallLaunchError("系统未能创建卸载程序进程。");
                return;
            }

            _deferredCommandGate.Close();
            _shuttingDown = true;
            _application.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _settings?.ShowUninstallLaunchError(ex.Message);
        }
    }

    private static (string Message, MessageBoxImage Icon) FormatDeletionResult(UserDataDeletionResult result) => result.Status switch
    {
        UserDataDeletionStatus.Deleted =>
            ("GlassDesk 用户数据已删除。程序即将退出；下次启动时会创建一份新的默认数据。", MessageBoxImage.Information),
        UserDataDeletionStatus.NotFound =>
            ("没有找到 GlassDesk 用户数据目录。程序即将退出。", MessageBoxImage.Information),
        UserDataDeletionStatus.Busy =>
            (result.Error ?? "数据仍被其它 GlassDesk 实例使用；本次没有删除任何数据。请退出其它实例后重试。", MessageBoxImage.Warning),
        UserDataDeletionStatus.UnsafePath =>
            ("数据目录路径校验未通过，本次没有删除数据。\n\n" + result.Error, MessageBoxImage.Error),
        UserDataDeletionStatus.Residual =>
            ($"数据删除未能完整完成。已删除 {result.DeletedEntries} 个条目；剩余内容已隔离在以下目录，请检查并手动处理：\n\n{result.ResidualDirectory}\n\nGlassDesk 不会在下次启动时自动继续删除。\n\n{result.Error}", MessageBoxImage.Warning),
        _ =>
            ("数据目录改名隔离失败；原数据保留，没有继续删除。\n\n" + result.Error, MessageBoxImage.Error),
    };
}
