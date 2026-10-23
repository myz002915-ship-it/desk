using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using GlassDesk.Core.Serialization;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 上次停留的设置页：欢迎回来时停在用户上次离开的页面。
/// </summary>
/// <remarks>
/// <para>
/// 刻意<strong>不</strong>放进 <see cref="Models.Settings.GlassDeskSettings"/>：
/// 那是一份用户可编辑、有 schema 版本、要参与备份/恢复的产品设置文档。
/// 「窗口记住了哪个标签页」是<strong>界面状态</strong>，不是产品设置——
/// 用户重置「外观」页不会期望这连带重置界面停留位置，备份/恢复也不该搬运它。
/// 因此这份记忆独立成一个小文档，损坏时静默按默认处理。
/// </para>
/// <para>
/// 序列化同样走源生成的 <c>JsonTypeInfo</c>，形状在编译期可见。
/// </para>
/// </remarks>
public sealed class PageMemoryStore
{
    private static readonly JsonTypeInfo<PageMemory> TypeInfo =
        GlassDeskJsonContext.Default.PageMemory;

    private readonly JsonFileStore<PageMemory> _store;

    public PageMemoryStore(string path, TimeProvider? timeProvider = null)
    {
        _store = new JsonFileStore<PageMemory>(path, TypeInfo, timeProvider);
    }

    public string FilePath => _store.Path;

    /// <summary>读取记忆的页面名；文档缺失或无法解析时返回 null（调用方落到默认页）。</summary>
    public string? Load()
    {
        var outcome = _store.Load(static () => new PageMemory());
        return outcome.Status == LoadStatus.Loaded ? outcome.Value.Page : null;
    }

    /// <summary>记录页面名。失败时静默：界面状态不值得让用户看到错误。</summary>
    public void Save(string page)
    {
        try
        {
            _store.Save(new PageMemory { Page = page });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }
}

/// <summary>持久化的界面状态：上次停留的设置页名。</summary>
public sealed class PageMemory
{
    public string? Page { get; set; }
}
