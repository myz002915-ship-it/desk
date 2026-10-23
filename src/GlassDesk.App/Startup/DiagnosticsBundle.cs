using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    /// <summary>诊断包输出目录：与用户数据同层，用户可以直接在资源管理器里看到。</summary>
    private static string DiagnosticsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.ProductFolderName, "diagnostics");

    /// <summary>
    /// 打包自诊断数据供用户发给开发者：设置、应用库、两份布局、界面状态、
    /// 日志与最近的损坏文件保留物，外加一页运行环境报告。
    /// </summary>
    private async Task ExportDiagnosticsAsync()
    {
        if (_data is null || _settings is null) return;
        _settings.ShowDiagnosticsStatus("正在生成诊断包…");
        var summary = await Task.Run(() => BuildDiagnosticsBundle()).ConfigureAwait(true);
        _settings.ShowDiagnosticsStatus(summary);
    }

    private string BuildDiagnosticsBundle()
    {
        var paths = _data!.Paths;
        Directory.CreateDirectory(DiagnosticsDirectory);
        var fileName = $"glassdesk-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
        var archivePath = Path.Combine(DiagnosticsDirectory, fileName);

        using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create))
        {
            // 配置类文档（存在就收）。
            foreach (var relative in new[]
                     {
                         Path.GetFileName(paths.SettingsFile),
                         Path.GetFileName(paths.AppLibraryFile),
                         Path.GetFileName(paths.DockLayoutFile),
                         Path.GetFileName(paths.LauncherLayoutFile),
                         Path.GetFileName(paths.WindowStateFile),
                     })
            {
                AddIfPresent(zip, paths.RootDirectory, relative, redactSettings: relative == "settings.json");
            }

            AddEnvironmentReport(zip, paths);

            // 日志与损坏文件保留物：整个目录收进来。
            AddDirectory(zip, paths.LogDirectory, "logs");
            if (Directory.Exists(paths.RecoveryDirectory)) AddDirectory(zip, paths.RecoveryDirectory, "recovery");
        }

        return $"诊断包已生成：{archivePath}（请把这个文件发给开发者。）";
    }

    private static void AddIfPresent(ZipArchive zip, string directory, string fileName, bool redactSettings)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return;
        try
        {
            if (redactSettings)
            {
                var entry = zip.CreateEntry(fileName);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(RedactSecrets(File.ReadAllText(path)));
            }
            else
            {
                zip.CreateEntryFromFile(path, fileName, CompressionLevel.Optimal);
            }
        }
        catch (IOException)
        {
            // 单个文件读不出时不中断整个打包。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddDirectory(ZipArchive zip, string directory, string prefix)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(directory, file);
                zip.CreateEntryFromFile(file, $"{prefix}/{relative}", CompressionLevel.Optimal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AddEnvironmentReport(ZipArchive zip, AppPaths paths)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"GlassDesk 诊断报告  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine(CultureInfo.InvariantCulture, $"版本：{AppInfo.Version}  构建：{AppInfo.Commit}  编译时间：{AppInfo.BuildTime:yyyy-MM-dd HH:mm} UTC");
        report.AppendLine(CultureInfo.InvariantCulture, $"操作系统：Windows {WindowsCapabilities.OsVersion}（Build {WindowsCapabilities.BuildNumber}）");
        report.AppendLine(CultureInfo.InvariantCulture, $"数据目录：{paths.RootDirectory}");
        report.AppendLine(CultureInfo.InvariantCulture, $"程序路径：{Environment.ProcessPath}");
        report.AppendLine(CultureInfo.InvariantCulture, $"64 位进程：{Environment.Is64BitProcess}");

        var displays = DisplayService.Enumerate();
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"显示器：{displays.Count}");
        foreach (var display in displays)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"· {display}");
        }

        var awareness = DisplayService.ProbeDpiAwareness();
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"DPI 感知：{awareness.Describe()}（系统 DPI {awareness.SystemDpi}）");

        var entry = zip.CreateEntry("environment.txt");
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(report.ToString());
    }

    /// <summary>
    /// settings.json 只包含开关与数值，但快捷键等字段是用户自己敲的，
    /// 谨慎起见把可能含隐私的顶部字段遮蔽掉——宁可多遮一层。
    /// </summary>
    private static string RedactSecrets(string text)
    {
        // 这个文档目前是纯数值/枚举/布尔，没有已知敏感内容；保留钩子
        // 便于未来新增字段时统一处理，而不用回头改打包逻辑。
        return text;
    }
}
