using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

// 磁贴磁贴拖拽重排 cluster（从 DockTileStrip.cs 拆出的成员，行为不变）。
public sealed partial class DockTileStrip : Border
{
    /// <summary>按下磁贴：记录起点与条目，等待移动阈值后才真正起拖。</summary>
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DockTileModel model })
        {
            return;
        }

        _pressedEntryId = model.EntryId;
        _pressPosition = e.GetPosition(this);
    }

    /// <summary>按住并移动超过阈值 → 进入拖放模态循环（阻塞到松手/取消）。</summary>
    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedEntryId is null || _dragActive
            || e.LeftButton != MouseButtonState.Pressed
            || _reorder is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var distance = (position - _pressPosition).Length;

        if (distance < 4)
        {
            return;
        }

        _dragActive = true;
        _droppedInStrip = false;
        _dragCancelReason = DragCancelReason.None;

        var draggedEntryId = _pressedEntryId;
        // 排序候选只有应用磁贴，固定本次拖动的起始槽位；插入边界始终
        // 依据拖动前的槽位计算，避免预览多次后目标索引跟着漂移。
        var candidateIds = ReorderCandidateIds.ToArray();
        _dragStartIndex = Array.IndexOf(candidateIds, draggedEntryId);
        _reorder.Begin(candidateIds);

        var data = new DataObject(TileDragFormat, draggedEntryId);
        data.SetData(DataFormats.Serializable, draggedEntryId);

        try
        {
            // 模态循环内部：DragOver/Drop/DragLeave/QueryContinueDrag 都会回到本控件。
            // Esc 取消由 OLE 处理（此时 DoDragDrop 返回 None），取消原因经
            // QueryContinueDrag 的 EscapePressed 记下来——只看位置判定会把
            // 「拖到栏外按 Esc」误当成移出取消固定。
            _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Move);

            // 手势结束了才做一次权威判定：拖拽期间一律不落盘（R-08）。
            var endedOutside = DragEndedOutside();
            var cancelled = ResolveCancelReason();

            var action = DockDragGesture.DecideEnd(_droppedInStrip, endedOutside, cancelled);

            if (action is DragEndAction.CancelOnly or DragEndAction.CancelAndUnpin && _reorder.IsActive)
            {
                _ = _reorder.Cancel();
            }

            if (action == DragEndAction.CancelAndUnpin && !string.IsNullOrEmpty(draggedEntryId))
            {
                UnpinRequested?.Invoke(this, draggedEntryId);
            }

            if (action != DragEndAction.KeepCommitted || !_reorder.IsActive)
            {
                ReorderFinished?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _dragActive = false;
            _droppedInStrip = false;
            _pressedEntryId = null;
            _dragStartIndex = -1;
        }
    }

    /// <summary>
    /// 本次拖拽是否被取消，并记下原因。
    /// </summary>
    /// <remarks>
    /// 两路信号，取到哪个算哪个：
    /// <list type="number">
    /// <item><c>QueryContinueDrag</c> 报的 <c>EscapePressed</c>——正路；</item>
    /// <item>手势结束时鼠标主键仍按着——Esc 取消的可观测形态（正常放下必然已松手）。
    /// 这一路是兜底：万一平台上第 1 路没触发，也不会把用户取消掉的条目删掉。</item>
    /// </list>
    /// </remarks>
    private bool ResolveCancelReason()
    {
        if (_dragCancelReason != DragCancelReason.None)
        {
            return true;
        }

        if (InputState.IsPrimaryMouseButtonDown())
        {
            _dragCancelReason = DragCancelReason.PrimaryButtonStillDown;
            return true;
        }

        return false;
    }

    /// <summary>
    /// OLE 拖放循环的状态通知：这里记下 Esc。
    /// </summary>
    /// <remarks>
    /// 必须挂在拖拽源元素上——WPF 只对发起拖拽的那个元素触发这个事件。
    /// </remarks>
    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragCancelReason = DragCancelReason.Escape;
        }
    }

    /// <summary>
    /// 手势结束时指针是否在栏外（含取消固定阈值）。
    /// </summary>
    /// <remarks>
    /// 用<strong>当刻光标位置</strong>判定，而不是拖拽过程中某次 <c>DragLeave</c> 的位置：
    /// 缓慢移出边界时那一次的位置未必达到阈值，同一个手势的结果会随鼠标速度而变。
    /// 光标是物理像素，经 WPF 的屏幕换算转成控件坐标（含 DPI）。
    /// </remarks>
    private bool DragEndedOutside()
    {
        if (DisplayService.GetCursorPixelPoint() is not { } cursor)
        {
            // 读不到光标（远控会话等）：按「仍在栏内」处理，宁可不动布局。
            return false;
        }

        Point local;

        try
        {
            local = PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (double.IsNaN(local.X) || double.IsNaN(local.Y))
        {
            return false;
        }

        return local.X < -UnpinDragDistance
            || local.X > ActualWidth + UnpinDragDistance
            || local.Y < -UnpinDragDistance
            || local.Y > ActualHeight + UnpinDragDistance;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        PreviewReorderAtEventPoint(e, entryId);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Handled = true;
        _droppedInStrip = true;

        // OLE 可能在松手前没有再派发一次 DragOver。Drop 自带最终指针坐标，
        // 用它刷新预览后再提交，避免把上一次 DragOver 的旧位置落盘。
        PreviewReorderAtEventPoint(e, entryId);

        // 松手 = 操作完成：状态机在这里统一落盘一次（R-08）。
        var outcome = _reorder.Commit();

        if (outcome == ReorderOutcome.SaveFailed)
        {
            // 失败已由状态机恢复原顺序；给用户一句能听懂的解释。
            System.Windows.MessageBox.Show(
                "排序没能保存，可能是数据文件暂时写不了。顺序已恢复原样。",
                "GlassDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ReorderFinished?.Invoke(this, EventArgs.Empty);
    }

    private void PreviewReorderAtEventPoint(DragEventArgs e, string entryId)
    {
        if (_dragStartIndex < 0 || _reorder?.IsActive != true)
        {
            return;
        }

        // OLE DragOver/Drop 参数携带的是本次事件的目标坐标；滚动偏移只加一次。
        var viewportX = e.GetPosition(_scroller).X;
        if (double.IsNaN(viewportX))
        {
            return;
        }

        var insertionIndex = IndexForInsert(ToContentX(viewportX));
        var targetIndex = insertionIndex > _dragStartIndex ? insertionIndex - 1 : insertionIndex;
        _reorder.PreviewMove(entryId, targetIndex);
    }

    private bool IsInternalTileDrag(IDataObject data, out string entryId)
    {
        if (_dragActive && data.GetDataPresent(TileDragFormat))
        {
            var value = data.GetData(TileDragFormat) as string;

            if (!string.IsNullOrEmpty(value))
            {
                entryId = value;
                return true;
            }
        }

        entryId = string.Empty;
        return false;
    }

    /// <summary>
    /// 内容坐标落在哪个插入边界：每枚磁贴中心的左侧插到前面，右侧插到后面。
    /// </summary>
    /// <remarks>
    /// 只在<strong>应用磁贴</strong>范围内取值：内置入口那一侧不是排序目标，
    /// 拖到那儿也只是「插到最后一个应用之后」。
    /// </remarks>
    private int IndexForInsert(double contentPosition)
    {
        if (AppTileCount == 0)
        {
            return 0;
        }

        var geometry = DockMagnification.Compute(_models.Count, _iconSize, _spacing, _peak, null);

        for (var index = 0; index < AppTileCount; index++)
        {
            if (contentPosition < geometry[index].Center)
            {
                return index;
            }
        }

        return AppTileCount;
    }
}
