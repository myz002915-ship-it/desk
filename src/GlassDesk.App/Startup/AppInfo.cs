using System.Reflection;

namespace GlassDesk.App;

/// <summary>Single source of truth for the product version shown to the user.</summary>
internal static class AppInfo
{
    private static string? _commit;
    private static DateTimeOffset? _buildTime;

    /// <summary>1.4.0 style product version derived from the informational version.</summary>
    public static string Version =>
        (typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0").Split('+')[0].Split('-')[0];

    /// <summary>Git revision embedded via `-p:SourceRevisionId` at build time, or "dev" when absent.</summary>
    public static string Commit => _commit ??= ExtractMetadata("SourceRevisionId") ?? "dev";

    /// <summary>Build timestamp embedded via `-p:SourceBuildTimeId` at build time, or null when absent.</summary>
    public static DateTimeOffset? BuildTime => _buildTime ??= ParseBuildTime();

    public static string FullLabel => BuildTime is { } time
        ? $"{Version} ({Commit}, {time:yyyy-MM-dd HH:mm} UTC)"
        : $"{Version} ({Commit})";

    private static string? ExtractMetadata(string key)
    {
        var value = typeof(AppHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTimeOffset? ParseBuildTime()
    {
        var raw = ExtractMetadata("SourceBuildTimeId");
        return DateTimeOffset.TryParse(raw, out var time) ? time : null;
    }
}
