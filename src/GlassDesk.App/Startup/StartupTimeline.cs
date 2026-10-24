using System.Diagnostics;

namespace GlassDesk.App.Startup;

/// <summary>
/// 启动各阶段的耗时记录。
/// </summary>
/// <remarks>
/// 只做一件事：给关键阶段打时间戳，最后按「累计 + 增量」的格式写进
/// <c>startup.log</c>。冷启动的优化必须先能测量——没有这份记录，
/// 「哪一步拖慢了首帧」就只能靠猜。
///
/// 每次 <see cref="Mark"/> 只是一次 <see cref="Stopwatch"/> 读取加一次小对象入列，
/// 对启动路径的影响可以忽略。
/// </remarks>
internal sealed class StartupTimeline
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(string Phase, double Milliseconds)> _marks = new(16);

    /// <summary>已经记录了多少个阶段。</summary>
    public int Count => _marks.Count;

    /// <summary>自时间线创建（即宿主构造）以来的毫秒数。</summary>
    public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;

    /// <summary>记录一个阶段的结束时刻。</summary>
    public void Mark(string phase) => _marks.Add((phase, _clock.Elapsed.TotalMilliseconds));

    /// <summary>
    /// 生成从第 <paramref name="from"/> 个阶段起的可读行，供启动日志追加。
    /// </summary>
    /// <remarks>
    /// 增量以上一个已记录阶段为基准，因此首帧前的这段时间线看的是
    /// 「每一阶段各花了多少」，而不是彼此无关的绝对时刻。
    /// </remarks>
    public IReadOnlyList<string> Describe(int from = 0)
    {
        if (from >= _marks.Count)
        {
            return [];
        }

        var start = Math.Max(0, from);
        var previous = start == 0 ? 0 : _marks[start - 1].Milliseconds;
        var lines = new List<string>(_marks.Count - start);

        for (var index = start; index < _marks.Count; index++)
        {
            var (phase, at) = _marks[index];
            lines.Add($"  {phase}：{at:F1} ms（+{at - previous:F1}）");
            previous = at;
        }

        return lines;
    }
}
