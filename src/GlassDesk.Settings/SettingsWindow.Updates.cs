using System.Windows;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    public event EventHandler? CheckForUpdatesRequested;

    /// <summary>用户要求导出诊断包；宿主负责收集与打包。</summary>
    public event EventHandler? DiagnosticsExportRequested;

    public void ShowUpdateStatus(string version, string status, bool checking = false)
    {
        ProductVersionText.Text = "GlassDesk " + version;
        UpdateStatusText.Text = status;
        CheckUpdateButton.IsEnabled = !checking && !_userDataDeletionPending;
    }

    /// <summary>诊断包生成的进度与结果（含最终路径）。</summary>
    public void ShowDiagnosticsStatus(string status) => DiagnosticsStatusText.Text = status;

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e) =>
        DiagnosticsExportRequested?.Invoke(this, EventArgs.Empty);
}
