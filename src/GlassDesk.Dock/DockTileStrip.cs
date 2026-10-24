using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>一枚磁贴在最后一次绘制帧里的<strong>实际</strong>绘制状态。</summary>
/// <param name="EntryId">条目 id。</param>
/// <param name="Scale">实际施加的缩放。</param>
/// <param name="ZIndex">实际的 <c>Panel.ZIndex</c>——越大越靠上，就是用户看到的覆盖关系。</param>
/// <param name="Left">实际的画布左缘。</param>
public readonly record struct PaintedTileState(string EntryId, double Scale, int ZIndex, double Left);

/// <summary>
/// Dock 的磁贴条：固定槽位布局、放大动画、拖拽排序与溢出滚动都住在这里。
/// </summary>
/// <remarks>
/// <para>
/// <b>几何唯一来源。</b>每一帧由 <see cref="DockMagnification.Compute"/> 算出
/// 所有磁贴的位置与缩放，绘制（Canvas 定位 + 缩放变换）与命中
/// （<see cref="DockMagnification.HitTest"/>）用<strong>同一份</strong>结果；
/// 点击目标不取 WPF 的事件 sender，而是用 HitTest 的判定——
/// 这从结构上保证了「看见 A 就点中 A」，动画中途也一样（D-05）。
/// </para>
///
/// <para>
/// <b>拖拽排序（D-02/R-08）。</b>磁贴拖拽用 WPF 自己的拖放（同窗口内），
/// 手势只翻译成 <see cref="DockReorderModel"/> 的三个动作：
/// 预览只改内存顺序（插入指示线），松手提交时由状态机统一落盘<strong>一次</strong>，
/// Esc 或拖出栏外取消时绝不落盘。落盘次数可断言的性质来自状态机，
/// 不靠界面层自觉。
/// </para>
///
/// <para>
/// <b>溢出（修订计划第 6 条）。</b>固定项的总宽度封顶于可用宽度
/// （目标屏工作区减去放大余量与内边距），超出部分沿 Dock 方向滚动
/// （当前四种停靠位置全部横排，所以是横向滚），滚轮支持，滚动条隐藏，
/// 两端用渐变提示还有内容。磁贴尺寸永不整体缩小。
/// </para>
///
/// <para>
/// 命中与指针坐标一律换算到<strong>内容坐标系</strong>（滚动偏移 + 视口内位置），
/// 滚动之后点击依然正确。
/// </para>
/// </remarks>
public sealed partial class DockTileStrip : Border
{
    /// <summary>内部拖拽数据格式：本进程磁贴排序拖放。</summary>
    private const string TileDragFormat = "GlassDesk-TileDrag";

    /// <summary>拖出这个距离（DIP，沿轴）就视为「拖出取消固定」（D-03）。</summary>
    private const double UnpinDragDistance = 80;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _canvas;
    private readonly Border _leadingFade;
    private readonly Border _trailingFade;

    private readonly List<DockTileModel> _models = [];
    private readonly List<Border> _tiles = [];

    /// <summary>每枚磁贴的悬停高光层，与 <see cref="_tiles"/> 一一对应。</summary>
    private readonly List<Border> _highlights = [];

    private readonly DockRenderLoop _animationLoop;
    private bool _pointerActive;
    private double _pointerPosition = double.NaN;
    private int _hoveredTileIndex = -1;

    private double _iconSize = 48;
    private double _spacing = 8;
    private double _peak = 1.15;
    private double _verticalPadding = 8;

    /// <summary>可用宽度上限，DIP。0 表示不限（由外层决定）。</summary>
    private double _maxContentWidth;

    /// <summary>磁贴被点击。参数是应用库条目 id。</summary>
    public event EventHandler<string>? TileClicked;

    /// <summary>磁贴被右键（D-04）。参数是应用库条目 id；内置入口不触发。</summary>
    public event EventHandler<string>? TileContextMenuRequested;

    /// <summary>拖拽提交完成（顺序已落盘）或取消，宿主应重建磁贴行。</summary>
    public event EventHandler? ReorderFinished;

    /// <summary>磁贴被拖出栏外（D-03）：请求取消固定，不删除库条目。</summary>
    public event EventHandler<string>? UnpinRequested;

    /// <summary>排序状态机由宿主注入：落盘委托在宿主手里（经布局存储保存一次）。</summary>
    private DockReorderModel? _reorder;
    private string? _pressedEntryId;
    private Point _pressPosition;
    private bool _dragActive;
    internal bool IsDragging => _dragActive;
    private int _dragStartIndex = -1;

    /// <summary>栏内是否收到过本次拖拽的 Drop（顺序已在那一刻提交）。</summary>
    private bool _droppedInStrip;

    /// <summary>本次拖拽的取消原因；没取消过为 <see cref="DragCancelReason.None"/>。</summary>
    private DragCancelReason _dragCancelReason;

    /// <summary>最近一次拖拽的取消原因。诊断与测试用。</summary>
    public DragCancelReason LastDragCancelReason => _dragCancelReason;

    /// <summary>
    /// 最近一次<strong>真正画到屏幕上</strong>的那一帧几何。
    /// </summary>
    /// <remarks>
    /// 命中的唯一依据（视觉即所得）：绘制用缓动后的指针位置，而点击发生在任意时刻，
    /// 若在点击时用即时指针重算一份几何，动画中途就会与屏幕上的画面分叉——
    /// 实测出现「已绘制几何命中第一枚、点击处理命中第二枚」。
    /// </remarks>
    private TileGeometry[] _paintedGeometry = [];
    private TileGeometry[] _targetGeometry = [];
    private int[] _paintOrder = [];
    private readonly IComparer<int> _paintOrderComparer;

    /// <summary>注入排序状态机并启用内部拖拽。</summary>
    public void SetReorderModel(DockReorderModel? reorder)
    {
        _reorder = reorder;
        AllowDrop = reorder is not null;
    }

    /// <summary>
    /// 参与排序的条目 id（<strong>只有应用库里的真实条目</strong>）。
    /// </summary>
    /// <remarks>
    /// 内置入口（启动台、显示桌面、回收站）固定在独立区域：不参与排序，
    /// 也不能被拖出移除。排序提交时写进布局的就是这个列表。
    /// </remarks>
    public IReadOnlyList<string> ReorderCandidateIds =>
        _models.Take(AppTileCount).Select(model => model.EntryId).ToList();

    /// <summary>应用磁贴的数量（磁贴行里前缀部分；其余是内置入口）。</summary>
    private int AppTileCount { get; set; }

    /// <summary>应用磁贴与内置入口之间的分隔线（视觉上把两组分开）。</summary>
    private readonly System.Windows.Shapes.Rectangle _groupDivider = new()
    {
        Width = 1,
        Fill = Brushes.Gray,
        Opacity = 0.5,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    public DockTileStrip()
    {
        _paintOrderComparer = Comparer<int>.Create((left, right) =>
            DockMagnification.CompareDrawOrder(_paintedGeometry, left, right));
        Background = Brushes.Transparent;
        ClipToBounds = true;

        _canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
        };

        _scroller = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        _scroller.ScrollChanged += (_, _) => UpdateFades();

        _leadingFade = CreateFade(0);
        _trailingFade = CreateFade(1);

        // Esc 取消的记录点：WPF 只对发起拖拽的元素触发这个事件，
        // 而 DoDragDrop 的源就是这个控件。
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));

        var overlay = new Grid { Background = Brushes.Transparent };
        _ = overlay.Children.Add(_scroller);
        _ = overlay.Children.Add(_leadingFade);
        _ = overlay.Children.Add(_trailingFade);

        Child = overlay;

        _animationLoop = new DockRenderLoop(OnFrame);
        Unloaded += (_, _) => ResetHover();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetHover(); };
    }

    /// <summary>
    /// 整行重建。磁贴集合变化时调用；尺寸设置走 <see cref="SetMetrics"/>。
    /// </summary>
    /// <remarks>
    /// 约定：<strong>前面的都是应用磁贴，最后的若干枚内置入口</strong>
    /// （id 带 <see cref="DockLayoutPolicy.EntryTilePrefix"/>）。分组在这里算一次，
    /// 之后排序、拖拽与分隔线都以它为准。
    /// </remarks>
    public void SetTiles(IReadOnlyList<DockTileModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _models.Clear();
        _models.AddRange(tiles);

        // 内置入口一定在末尾（DockWindow 就是这么排的），但这里不假定——
        // 按 id 前缀数出前缀部分的应用数，遇到第一个入口就收尾。
        AppTileCount = 0;

        foreach (var model in _models)
        {
            if (DockLayoutPolicy.IsEntryTile(model.EntryId))
            {
                break;
            }

            AppTileCount++;
        }

        _canvas.Children.Clear();
        _tiles.Clear();
        _highlights.Clear();

        for (var index = 0; index < _models.Count; index++)
        {
            // 只有应用磁贴挂拖拽处理器：内置入口不参与排序，也不能被拖出移除。
            var draggable = index < AppTileCount;
            var tile = CreateTile(_models[index], draggable);
            _tiles.Add(tile);
            _ = _canvas.Children.Add(tile);
        }

        _ = _canvas.Children.Add(_groupDivider);

        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        _hoveredTileIndex = -1;
        UpdateTileLayout();
        PaintFrame(null);
    }

    /// <summary>应用尺寸设置并重排。滚动封顶也在这里生效。</summary>
    public void SetMetrics(double iconSize, double spacing, double peak, double verticalPadding, double maxContentWidth)
    {
        _iconSize = Math.Max(1, iconSize);
        _spacing = Math.Max(0, spacing);
        _peak = Math.Max(1, peak);
        _verticalPadding = Math.Max(0, verticalPadding);
        _maxContentWidth = Math.Max(0, maxContentWidth);

        // 封顶必须落到**实际布局**上，否则视口会随内容无限变宽：
        // 磁贴只会被窗口裁掉、滚不动也点不到。这里把上限设在控件自身的 MaxWidth 上，
        // 内部的 ScrollViewer 因此拿到有限视口，横向滚动条（隐藏样式）才真正工作。
        MaxWidth = _maxContentWidth > 0 ? _maxContentWidth : double.PositiveInfinity;

        UpdateTileLayout();
        PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>指针离开磁贴条：所有磁贴平滑回到未放大。</summary>
    public void NotifyPointerLeft()
    {
        _pointerActive = false;
        _hoveredTileIndex = -1;
        EnsureAnimating();
    }

    /// <summary>当前内容总宽（DIP，含两端放大越界）。供外层决定窗口尺寸。</summary>
    public double RequiredLength =>
        DockMagnification.RequiredLength(_models.Count, _iconSize, _spacing, _peak);

    /// <summary>垂直于 Dock 轴需要的厚度（DIP，含放大空间）。</summary>
    public double RequiredThickness =>
        DockMagnification.RequiredThickness(_iconSize, _peak, _verticalPadding);

    /// <summary>内容是否超出了封顶宽度。</summary>
    public bool CanScroll => _maxContentWidth > 0 && RequiredLength > _maxContentWidth;

    /// <param name="draggable">
    /// 是否参与排序拖拽。内置入口传 false：它们固定在独立区域，
    /// 既不参与应用排序，也不能被拖出移除。
    /// </param>
    private Border CreateTile(DockTileModel model, bool draggable)
    {
        FrameworkElement body;


        if (model.Icon is { } iconSource)
        {
            var icon = new Image
            {
                Source = iconSource,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = model.IsMissing ? 0.4 : 1,
            };
            Bind(icon, Image.WidthProperty, model, nameof(DockTileModel.IconSize));
            Bind(icon, Image.HeightProperty, model, nameof(DockTileModel.IconSize));
            body = icon;
        }
        else
        {
            // 内置入口（D-12/D-13/D-14）没有提取来的图标文件，
            // 用系统自带的 Segoe Fluent Icons 字形绘制，零依赖。
            body = new TextBlock
            {
                Text = model.Glyph ?? "?",
                Opacity = model.IsMissing ? 0.4 : 1,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(body, TextBlock.FontSizeProperty, model, nameof(DockTileModel.IconSize));
        }

        // Expose the visible body through UIA; a Border itself has no automation peer.
        System.Windows.Automation.AutomationProperties.SetName(body, model.DisplayName);
        System.Windows.Automation.AutomationProperties.SetAutomationId(body, "dock-tile:" + model.EntryId);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Bind(dot, System.Windows.Shapes.Shape.FillProperty, model, nameof(DockTileModel.DotBrush));
        Bind(dot, VisibilityProperty, model, nameof(DockTileModel.RunningVisibility));

        // 图标下面垫一层悬停高光：静止时完全透明，指针悬停时随放大一起浮现。
        // 用负外边距把它撑出图标轮廓外一点，看起来是「图标被托起来」，而不是被框住。
        var highlight = new Border
        {
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(-6),
            Background = ThemeService.Current?.Palette.HoverOverlayBrush,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var bodyHost = new Grid();
        _ = bodyHost.Children.Add(highlight);
        _ = bodyHost.Children.Add(body);
        _highlights.Add(highlight);

        var content = new StackPanel();
        _ = content.Children.Add(bodyHost);
        _ = content.Children.Add(dot);

        var tile = new Border
        {
            Child = content,
            RenderTransform = new ScaleTransform(1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = model.ToolTipText,
            DataContext = model,
        };

        // 拖拽处理器只挂在应用磁贴上。内置入口没有拖拽，也就没有「拖出移除」这条路。
        if (draggable)
        {
            tile.MouseLeftButtonDown += OnTileMouseDown;
            tile.MouseMove += OnTileMouseMove;
        }

        tile.MouseLeftButtonUp += OnTileMouseUp;
        tile.MouseRightButtonUp += OnTileContextMenu;
        return tile;
    }


    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        // 命中不以 sender 为准，也不在这里重算几何：用**最后一次绘制帧**做判定
        // （视觉即所得）。重算会在动画中途与屏幕上的画面分叉。
        // 换算走「视口坐标 → 内容坐标」这一条路（只加一次偏移）。
        var viewportX = PointerViewportPosition();

        if (viewportX is not { } position)
        {
            return;
        }

        var hit = ResolveHitAtViewportX(position);

        if (hit >= 0 && hit < _models.Count)
        {
            TileClicked?.Invoke(this, _models[hit].EntryId);
        }
    }

    private void OnTileContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: DockTileModel { EntryId: var entryId } }
            && !entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            TileContextMenuRequested?.Invoke(this, entryId);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        _pointerActive = true;

        // 目标位置先记录，插值交给帧循环——鼠标事件风暴不再每条都触发重排。
        // WPF 偶尔给出 NaN 坐标（正在布局），那就保持上一个目标。
        if (PointerContentPosition() is { } contentX)
        {
            _pointerPosition = contentX;
        }

        EnsureAnimating();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        NotifyPointerLeft();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (CanScroll)
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 指针在<strong>视口</strong>内的位置换算到内容坐标；读不到时为 null。
    /// </summary>
    /// <remarks>
    /// <strong>换算只有这一处，也只加一次偏移。</strong>
    /// <c>Mouse.GetPosition(_canvas)</c> 拿到的已经是内容坐标——画布是滚动内容的子元素，
    /// 它的坐标空间本身已经包含了滚动位移；再走一次 <see cref="ToContentX"/>
    /// 会把偏移加两遍。实测：滚动 120 DIP、视口位置 50 时算成 290（正确是 170），
    /// 表现为滚动后的点击、放大与拖拽插入位置整体偏移。
    /// </remarks>
    private double? PointerViewportPosition()
    {
        // 相对本控件（= 视口）取坐标，不相对画布。
        var position = Mouse.GetPosition(this);

        return double.IsNaN(position.X) ? null : position.X;
    }

    /// <summary>指针在滚动画布内容坐标系内的位置，已包含实际滚动变换。</summary>
    private double? PointerContentPosition()
    {
        // 画布自身的坐标变换已经反映 ScrollViewer 偏移；这里不能再额外加偏移。
        var position = Mouse.GetPosition(_canvas);

        return double.IsNaN(position.X) ? null : position.X;
    }

    /// <summary>
    /// 把<strong>视口内</strong>的 x 换算成内容坐标（加上当前滚动偏移）。
    /// </summary>
    /// <remarks>
    /// 输入必须是视口坐标。相对画布读到的位置已经是内容坐标，不要再经过这里——
    /// 那会把偏移加两遍（见 <see cref="PointerViewportPosition"/>）。
    /// </remarks>
    public double ToContentX(double viewportX) => viewportX + _scroller.HorizontalOffset;

    /// <summary>
    /// 用<strong>视口内</strong>的坐标解析命中（滚动后仍正确）。
    /// </summary>
    /// <remarks>
    /// 点击、悬停与拖拽插入都走这一条路，因此「只加一次偏移」这条性质
    /// 在测试里覆盖的就是运行时真正执行的代码，而不是另写一份等价逻辑。
    /// </remarks>
    public int ResolveHitAtViewportX(double viewportX) => ResolveHit(ToContentX(viewportX));

    /// <summary>滚动到指定横向偏移（DIP）。诊断与测试用。</summary>
    public void ScrollToHorizontalOffset(double offset) => _scroller.ScrollToHorizontalOffset(offset);

    /// <summary>当前横向滚动偏移（DIP）。诊断与测试用。</summary>
    public double HorizontalOffset => _scroller.HorizontalOffset;

    /// <summary>当前视口宽度（DIP）。诊断与测试用。</summary>
    public double ViewportWidth => _scroller.ViewportWidth;

    /// <summary>滚到最右端。诊断与测试用。</summary>
    public void ScrollToEnd() => _scroller.ScrollToRightEnd();

    private void EnsureAnimating()
    {
        if (IsVisible) _animationLoop.Start();
        else PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>动画帧：所有磁贴同时追随当前指针对应的目标缩放，全部到位后停帧。</summary>
    private bool OnFrame(double deltaSeconds)
    {
        var targetPointer = CurrentPointerOrNull();
        // Resolve the latest pointer once per frame, rather than for every queued mouse event.
        _hoveredTileIndex = targetPointer is { } position ? FindHoveredTile(position) : -1;
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            PaintFrame(targetPointer);
            return true;
        }
        return AnimateFrame(targetPointer, deltaSeconds);
    }

    private void ResetHover()
    {
        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        _hoveredTileIndex = -1;
        PaintFrame(null);
    }

    /// <summary>
    /// 按给定指针位置绘制一帧，并把这一帧的几何缓存给命中使用。
    /// </summary>
    /// <remarks>
    /// 缓动由帧循环负责；这个方法只做「给定指针 → 几何 → 屏幕」这一步，
    /// 因此可以在测试里确定性地重放某一帧，验证点击与画面一致。
    /// </remarks>
    public void PaintFrame(double? pointerPosition)
    {
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(_paintedGeometry, pointerPosition);
        PaintGeometry(_paintedGeometry);
        // 非动画帧（复位、静态重绘）上高光不做插值，直接对齐当前悬停目标。
        _ = PaintHighlights(deltaSeconds: 0, animate: false);
    }

    private bool AnimateFrame(double? pointerPosition, double deltaSeconds)
    {
        var targets = _targetGeometry;
        DockMagnification.ComputeInto(targets, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(targets, pointerPosition);
        var geometry = _paintedGeometry;
        var allSettled = true;

        for (var index = 0; index < targets.Length; index++)
        {
            var currentScale = _tiles[index].RenderTransform is ScaleTransform transform
                ? transform.ScaleX
                : 1.0;
            var targetScale = targets[index].Scale;
            var nextScale = DockHoverScale.Advance(currentScale, targetScale, deltaSeconds);

            if (nextScale != targetScale)
            {
                allSettled = false;
            }

            geometry[index] = new TileGeometry(targets[index].Center, nextScale);
        }

        PaintGeometry(geometry);
        var highlightsSettled = PaintHighlights(deltaSeconds, animate: true);
        return allSettled && highlightsSettled;
    }

    /// <summary>
    /// 让每枚磁贴的悬停高光淡入淡出。
    /// </summary>
    /// <remarks>
    /// 只有被指针指到的那一枚点亮，淡出同样走帧循环，因此指针横扫过整行时
    /// 高光是依次亮起又依次熄灭，而不是逐帧闪烁。
    /// 返回是否全部到位——帧循环据此决定什么时候停。
    /// </remarks>
    private bool PaintHighlights(double deltaSeconds, bool animate)
    {
        var settled = true;

        for (var index = 0; index < _highlights.Count && index < _models.Count; index++)
        {
            var target = index == _hoveredTileIndex ? 1.0 : 0.0;
            var highlight = _highlights[index];
            var current = highlight.Opacity;
            var next = animate ? DockHoverScale.Advance(current, target, deltaSeconds) : target;

            if (next != target)
            {
                settled = false;
            }

            if (Math.Abs(current - next) > 0.0005)
            {
                highlight.Opacity = next;
            }
            else if (!animate && current != next)
            {
                highlight.Opacity = next;
            }
        }

        return settled;
    }

    private void ApplyHoveredPeak(TileGeometry[] geometry, double? pointerPosition)
    {
        if (pointerPosition is null || _hoveredTileIndex < 0 || _hoveredTileIndex >= geometry.Length)
        {
            return;
        }

        geometry[_hoveredTileIndex] = new TileGeometry(geometry[_hoveredTileIndex].Center, _peak);
    }

    private int FindHoveredTile(double contentPosition)
    {
        var nearestIndex = -1;
        var nearestDistance = double.PositiveInfinity;
        var halfExpandedWidth = _iconSize * _peak / 2;

        for (var index = 0; index < _models.Count; index++)
        {
            var center = DockMagnification.SlotCenter(index, _iconSize, _spacing, _peak);
            var distance = Math.Abs(contentPosition - center);
            if (distance <= halfExpandedWidth && distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private void UpdateTileLayout()
    {
        var count = _models.Count;
        if (_paintedGeometry.Length != count)
        {
            _paintedGeometry = new TileGeometry[count];
            _targetGeometry = new TileGeometry[count];
            _paintOrder = Enumerable.Range(0, count).ToArray();
        }
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, null);
        _canvas.Width = DockMagnification.RequiredLength(count, _iconSize, _spacing, _peak);
        _canvas.Height = RequiredThickness;
        var halfOverhangThickness = (_canvas.Height - _iconSize) / 2;
        for (var index = 0; index < count; index++)
        {
            Canvas.SetLeft(_tiles[index], _paintedGeometry[index].Center - _iconSize / 2);
            Canvas.SetTop(_tiles[index], halfOverhangThickness);
        }
        UpdateGroupDivider(_paintedGeometry);
        UpdateFades();
    }

    private void PaintGeometry(TileGeometry[] geometry)
    {
        // z 序取绘制与命中共同的那一份：缩放大的在上，重叠处可见者即被点中者。
        var order = _paintOrder;
        Array.Sort(order, _paintOrderComparer);

        for (var rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var tile = _tiles[index];
            var scale = geometry[index].Scale;

            // order 的第 0 项是「最上层」，而 ZIndex 越大越靠上——所以是**取反**赋值。
            // 早先写成 Panel.SetZIndex(tile, rank) 时，递增的 rank 把最小的磁贴放到了最上层，
            // 绘制层级与命中顺序恰好相反（重叠区「看见 A 点中 B」）。
            var zIndex = order.Length - rank;
            if (Panel.GetZIndex(tile) != zIndex) Panel.SetZIndex(tile, zIndex);

            if (tile.RenderTransform is ScaleTransform transform)
            {
                if (transform.ScaleX != scale) transform.ScaleX = scale;
                if (transform.ScaleY != scale) transform.ScaleY = scale;
            }
        }

    }

    /// <summary>
    /// 把分隔线放在「应用磁贴」与「内置入口」两组的中间。
    /// </summary>
    /// <remarks>
    /// 只有两组都在时才显示。位置取两组相邻两枚磁贴槽位中心的中点——
    /// 与磁贴一样落在画布坐标里，因此滚动时跟随内容。
    /// </remarks>
    private void UpdateGroupDivider(TileGeometry[] geometry)
    {
        if (AppTileCount == 0 || AppTileCount >= geometry.Length)
        {
            _groupDivider.Visibility = Visibility.Collapsed;
            return;
        }

        var lastAppCenter = geometry[AppTileCount - 1].Center;
        var firstEntryCenter = geometry[AppTileCount].Center;

        Canvas.SetLeft(_groupDivider, (lastAppCenter + firstEntryCenter) / 2);
        Canvas.SetTop(_groupDivider, _iconSize * 0.15);
        _groupDivider.Height = _iconSize * 0.7;
        _groupDivider.Visibility = Visibility.Visible;

        // 分隔线不参与命中的重叠归属（它不可点击），层级压在磁贴之下即可。
        Panel.SetZIndex(_groupDivider, -1);
    }

    /// <summary>
    /// 按<strong>最后一次绘制帧</strong>解析指针命中的磁贴；没有命中返回 -1。
    /// </summary>
    /// <param name="pointerPosition">指针沿 Dock 轴的位置（画布坐标，DIP）。</param>
    public int ResolveHit(double pointerPosition)
        => _paintedGeometry.Length == 0
            ? -1
            : DockMagnification.HitTest(_paintedGeometry, _iconSize, pointerPosition);

    /// <summary>最后一次绘制帧的几何快照（诊断与测试用）。</summary>
    public IReadOnlyList<TileGeometry> PaintedGeometry => _paintedGeometry.ToArray();

    /// <summary>
    /// 最后一次绘制帧里每枚磁贴的<strong>实际</strong>绘制状态（读的是可视树上的真值）。
    /// </summary>
    /// <remarks>
    /// 刻意读 <c>Panel.GetZIndex</c> 与 <c>Canvas.GetLeft</c>，而不是把算出来的数组再报一遍：
    /// 「绘制层级与命中顺序相反」这个缺陷之所以能溜过测试，正是因为当时的断言核对的是
    /// 几何顺序数组（派生量），没有核对真正生效的层级。诊断与测试要读生效值。
    /// </remarks>
    public IReadOnlyList<PaintedTileState> PaintedTiles
    {
        get
        {
            var states = new List<PaintedTileState>(_tiles.Count);

            for (var index = 0; index < _tiles.Count; index++)
            {
                var tile = _tiles[index];
                var scale = tile.RenderTransform is ScaleTransform transform ? transform.ScaleX : 1.0;
                var entryId = index < _models.Count ? _models[index].EntryId : string.Empty;

                states.Add(new PaintedTileState(entryId, scale, Panel.GetZIndex(tile), Canvas.GetLeft(tile)));
            }

            return states;
        }
    }


    private double? CurrentPointerOrNull()
        => _pointerActive && !double.IsNaN(_pointerPosition) ? _pointerPosition : null;

    /// <summary>两端渐隐提示：可滚动且还没滚到头的一侧显示。</summary>
    private void UpdateFades()
    {
        var scrollable = CanScroll;

        _leadingFade.Visibility = scrollable && _scroller.HorizontalOffset > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        var maxOffset = Math.Max(0, _scroller.ExtentWidth - _scroller.ViewportWidth);
        _trailingFade.Visibility = scrollable && _scroller.HorizontalOffset < maxOffset - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static Border CreateFade(double direction)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(direction, 0.5),
            EndPoint = new Point(1 - direction, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x88, 0x00, 0x00, 0x00), 0),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1),
            },
        };

        return new Border
        {
            Background = gradient,
            Width = 28,
            HorizontalAlignment = direction < 0.5 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
    }

    private static void Bind(FrameworkElement element, DependencyProperty property, object source, string path)
    {
        _ = element.SetBinding(property, new System.Windows.Data.Binding
        {
            Source = source,
            Path = new PropertyPath(path),
        });
    }
}
