using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NecrologistMascot.Services;
using SkiaSharp;
using Spine;

namespace NecrologistMascot.Rendering;

/// <summary>
/// 桌宠渲染控件：CPU 光栅 SKSurface → 直接拷进 WriteableBitmap，交给 Avalonia 显示。
/// 不做 PNG 编解码（旧实现每帧 PNG 编码约 10~20ms，是最大的 CPU 浪费）。
/// 30fps 更新，复用 surface 与位图，避免每帧分配。
/// </summary>
public sealed class PetRenderControl : Control, IDisposable
{
    private readonly SkeletonModel _model;
    private readonly SpineCanvasRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// 帧级追踪开关（环境变量 NECRO_TRACE=1 打开）。
    /// 每 10 帧记一条：窗口像素尺寸、锚点比例、脚底线位置、控件尺寸。
    /// 定位"角色抖动"时看这行有没有周期性变化即可 —— 全都不变就说明抖动不在渲染层。
    /// </summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("NECRO_TRACE") == "1";
    private int _traceTick;

    private WriteableBitmap? _wbmp;
    private SKSurface? _surface;
    private int _surfaceW, _surfaceH;
    private double _lastSeconds;
    private double _cachedScaling = 1.0;
    private bool _disposed;

    // 视框：以「静态包围盒中心」为中心的半宽/半高，可随动作幅度扩张。
    //
    // 为什么只存半宽半高、中心固定：
    //   若像原来那样存 minX/minY/maxX/maxY 四边，四边各自独立插值，
    //   视框中心就会漂移，绘制时角色跟着左右上下跑（"偏移"）。
    //   锁定中心后，视框永远对称扩张，角色始终在窗口正中间。
    private float _halfW, _halfH;

    /// <summary>上一次视框扩张的时刻。收缩前要等一小会儿，避免连续动作之间窗口来回缩涨。</summary>
    private float _lastGrowTime;

    // 算当前帧包围盒用的临时缓冲（够长就不重新分配）
    private float[] _boundsBuf = new float[256];

    // 安全边距：绘制框相对「静态包围盒」的放大比例，四周各留这么多。
    // 横向给得多一些 —— 技能动画大多是左右挥动，纵向主要是轻微起伏。
    // ⚠ 这两个值同时决定窗口尺寸和绘制比例，两边必须一致（见 RenderFrame 里的说明）。
    public const double PadX = 0.25;
    public const double PadY = 0.12;

    /// <summary>
    /// 固定框模式（motion / absolute）下的边距。
    ///
    /// 那两个框本身就是「逐帧顶点的并集」，已经覆盖到最远的一个像素，
    /// 再按动态模式的 25%/12% 留白纯属浪费 —— 只留 3% 给抗锯齿就够了。
    /// 动态模式必须保留大边距：它的框是分位框，要留提前量给「动作比框大」的那几帧。
    /// </summary>
    public const double FixedPad = 0.03;

    /// <summary>
    /// 角色「脚底线」到窗口下沿的固定距离（DIP）。
    /// 必须是常数，不能写成窗口高度的比例 —— 窗口会随动作长高，
    /// 用比例的话脚会跟着往下滑。用常数时，只要窗口底边不动，脚就绝对不动。
    /// </summary>
    public const double GroundInset = 12;

    /// <summary>
    /// 脚底到窗口下沿的实际距离（DIP）。
    ///
    /// 平时就是 <see cref="GroundInset"/>；但如果模型在锚点（脚底）下方还有可见部分
    /// （脚下光圈、拖地裙摆），换算成 DIP 后超过 12，就自动放大到刚好容纳 + 4 DIP 余量。
    /// 对「给定模型 + 给定缩放」它是个常数，所以「窗口长高时脚不往下滑」这条仍然成立。
    /// </summary>
    public double GroundInsetDips
    {
        get
        {
            // 跟着当前取框模式走：窗口用的是哪个框，脚下留白就按哪个框算，
            // 否则换成「绝对框」模式后，框下沿比角色实际低很多，脚会看起来悬空。
            double below = (_model.AnchorY - StaticBox.MinY) * Scale;
            return Math.Max(GroundInset, below + 4);
        }
    }

    /// <summary>
    /// 运行时视框相对静态框的最大扩张倍率。
    ///
    /// 静态框经过分位数保护（砍掉最极端的 10% 帧），所以个别动作会比它大不少：
    /// 实测 303701 的 skill3 横向需要 3.86 倍静态半框（≈ 留边后可用宽度的 206%），
    /// 303702 的 skill2 需要 3.65 倍 —— 所以取 4.0 才装得下这些「手/道具甩出去」的动作。
    /// 它同时是安全阀：born 这种「从画面外飞入」的动画需要的倍率远大于 4，
    /// 被它截断后就自然被裁在画面外 —— 那本来就是期望效果。
    /// PetRenderControl 与 Diagnostics.Fit 共用这一个常量，改一处即可。
    /// </summary>
    public const float MaxExpand = 4.0f;

    /// <summary>
    /// 窗口取框模式：
    ///   motion   —— 「动作范围框」（默认）。剔除骨架整体位移的帧后取并集。
    ///               窗口一次定死、覆盖所有原地动作，不随动作伸缩，也不裁动作。
    ///   absolute —— 纯绝对框（所有帧的顶点并集）。一定会覆盖每一帧，
    ///               但把「角色飞出画面」的位移帧也算进去，窗口会白宽将近一倍。
    ///   dynamic  —— 分位框 + 随动作扩张（旧行为）。窗口最小，但会不停伸缩。
    /// </summary>
    public string FrameMode { get; set; } = FrameModes.Absolute;

    /// <summary>当前模式下用作「静态框」的包围盒。</summary>
    private (float MinX, float MinY, float MaxX, float MaxY) StaticBox => FrameMode switch
    {
        FrameModes.Absolute => _model.AbsBounds,
        FrameModes.Dynamic => _model.Bounds,
        _ => _model.MotionBounds,
    };

    /// <summary>只有 dynamic 模式才需要每帧算视框扩张；另外两种模式尺寸恒定。</summary>
    public bool IsDynamicMode => string.Equals(FrameMode, FrameModes.Dynamic, StringComparison.OrdinalIgnoreCase);

    /// <summary>锚点左侧需要多少（Spine 单位）。</summary>
    private float BoxLeft => Math.Max(1e-3f, _model.AnchorX - StaticBox.MinX);

    /// <summary>锚点右侧需要多少（Spine 单位）。</summary>
    private float BoxRight => Math.Max(1e-3f, StaticBox.MaxX - _model.AnchorX);

    /// <summary>
    /// 锚点在窗口里的水平位置（0 = 最左，1 = 最右）。
    ///
    /// 动态模式固定 0.5：它要靠「对称扩张」保证角色始终在窗口正中，否则每次 resize
    /// 角色都会左右跑一下。
    ///
    /// 固定框模式改成按框的不对称度落位：讣告人的绝对框左边要 506、右边只要 118
    /// （大招把角色甩到画面左侧），硬钉中间会让窗口宽度 = 506×2，右边 388 单位纯属浪费 ——
    /// 角色只占窗口宽度的 12%。按比例落位后窗口宽度变成 506+118=624，角色占 37%，
    /// 而且仍然一帧都不裁。锚点是静态的，不会随帧移动，所以不会有"跑来跑去"的问题。
    /// </summary>
    public double AnchorRatioOnWindow => IsDynamicMode ? 0.5 : BoxLeft / (BoxLeft + BoxRight);

    /// <summary>桌宠缩放（DIP）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>是否允许渲染循环运行（窗口隐藏时应置 false，避免空跑）。</summary>
    public bool Running { get; set; } = true;

    /// <summary>上下浮动（轻微正弦漂浮，像在呼吸）。</summary>
    public bool Bobbing { get; set; }

    /// <summary>水平翻转（左右镜像）。</summary>
    public bool Flip { get; set; }

    /// <summary>
    /// 视框是否跟随动作幅度扩张。关掉则永远用静态包围盒（窗口尺寸恒定）。
    /// </summary>
    public bool DynamicView { get; set; } = true;

    public SkeletonModel Model => _model;

    public PetRenderControl(SkeletonModel model)
    {
        _model = model;
        // 初始视框与 ResetView 用同一套口径（锚点到最远边），
        // 否则第一帧的窗口尺寸会和实际绘制的锚点对不上。
        ResetView();
        // 30fps：桌面挂件足够顺，且比 60fps 省一半 CPU
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnFrame);
        _timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!Running || _disposed) return;

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastSeconds, 0.0, 0.1);
        _lastSeconds = now;

        _model.Update(dt);
        RenderFrame();
        InvalidateVisual();
    }

    /// <summary>
    /// 控件期望的 DIP 尺寸 = 视框 × 缩放 × (1 + 边距)。
    /// 这里的边距和 RenderFrame 里的绘制框用的是同一组常量，两边必须保持同步，
    /// 否则会算出一边贴边、另一边有余量的结果。
    /// </summary>
    public Size DesiredPetSize
    {
        get
        {
            // 宽度：锚点在水平中心，左右各留 _halfW，所以是 2 倍。
            double padX = IsDynamicMode ? PadX : FixedPad;
            double padY = IsDynamicMode ? PadY : FixedPad;

            // 动态模式左右对称（角色居中）；固定模式按「左需求 + 右需求」算，不留对称冗余
            double widthUnits = IsDynamicMode ? _halfW * 2.0 : (BoxLeft + BoxRight);
            double w = widthUnits * Scale;

            // 高度：锚点（脚底）钉在窗口下沿上方 GroundInset 处，角色只往上长，
            // 所以只需要 1 倍 _halfH，不能再乘 2。
            //
            // ⚠ 这里曾经写成 _halfH * 2.0 * Scale（照抄宽度那一行），结果窗口高度
            // 是实际需要的两倍 —— 角色只占窗口下半部，上半部是一整只角色高度的
            // 透明空白，既点不到也拖不动，视觉上像"蹲在窗口下半截"。
            double h = _halfH * Scale;

            return new Size(Math.Max(1, w * (1 + padX)),
                            Math.Max(1, h * (1 + padY) + GroundInsetDips));
        }
    }

    private void RenderFrame()
    {
        // 缩放系数缓存：不需要每帧去查 TopLevel
        var top = TopLevel.GetTopLevel(this);
        if (top != null) _cachedScaling = top.RenderScaling > 0 ? top.RenderScaling : 1.0;

        // 与 PetWindow 里 SnapDip / newPxW、newPxH 的算法保持一致（都是四舍五入），
        // 否则两处差 1px，连续 resize 时会看到 1px 级的抖动。
        int pw = Math.Max(1, (int)Math.Round(Bounds.Width * _cachedScaling, MidpointRounding.AwayFromZero));
        int ph = Math.Max(1, (int)Math.Round(Bounds.Height * _cachedScaling, MidpointRounding.AwayFromZero));
        if (pw < 2 || ph < 2) return; // 尚未完成布局

        if (_surface == null || _surfaceW != pw || _surfaceH != ph)
        {
            _surface?.Dispose();
            _surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            _surfaceW = pw;
            _surfaceH = ph;
        }

        var surface = _surface;
        if (surface == null) return;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 只有「随动作伸缩」模式才需要每帧重算视框。
        // 固定框模式下 _halfW/_halfH 是常量：再每帧跑一遍收敛逻辑，
        // 等于让 DesiredPetSize 也跟着每帧重算一次浮点 —— 没有任何收益，只留抖动风险。
        if (IsDynamicMode) UpdateView();

        // 锚点 = 「身体中轴 + 脚底」，不再用包围盒中心。
        //
        // 包围盒中心会随头顶留白、披风、武器、脚下特效漂移，换皮肤时角色
        // 就会上下左右跳。中轴 + 脚底是角色自带的特征，几套皮肤基本一致，
        // 拿它当基准才能让不同皮肤站在同一个位置。
        float ax = _model.AnchorX;
        float ay = _model.AnchorY;

        canvas.Save();

        // 上下浮动：屏幕空间的正弦偏移（物理像素），与模型缩放无关
        float bobPx = Bobbing
            ? (float)Math.Sin(_clock.Elapsed.TotalSeconds * 1.8) * 5f * (float)_cachedScaling
            : 0f;

        // 脚底线钉在窗口下沿上方 GroundInsetDips 处；
        // 水平位置由 AnchorRatioOnWindow 决定（动态模式=正中，固定框模式=按框的不对称度）
        float groundPx = ph - (float)(GroundInsetDips * _cachedScaling) + bobPx;
        canvas.Translate(pw * (float)AnchorRatioOnWindow, groundPx);

        // 绘制比例只由「用户缩放 × 屏幕 DPI」决定，与窗口尺寸彻底解耦。
        //
        // 这里刻意**不做**「窗口装不下就缩小」的钳制：那个钳制会在窗口追上的那一帧
        // 把角色猛地缩一下再弹回，正是之前看到的"振动"。窗口尺寸最多晚一帧（33ms），
        // 短暂越界远比尺寸抖动更难被察觉。
        float s = (float)(Scale * _cachedScaling);

        // 水平翻转：X 方向取负
        canvas.Scale(Flip ? -s : s, -s);
        canvas.Translate(-ax, -ay);
        _renderer.Draw(canvas, _model.Skeleton, _model);
        canvas.Restore();
        canvas.Flush();

        // 零拷贝：像素直接搬进 WriteableBitmap，不做 PNG 编解码
        using var pixmap = surface.PeekPixels();
        if (pixmap == null) return;

        if (_wbmp == null || _wbmp.PixelSize.Width != pw || _wbmp.PixelSize.Height != ph)
        {
            _wbmp?.Dispose();
            _wbmp = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
                                        PixelFormat.Rgba8888, AlphaFormat.Premul);
        }

        if (Trace && (++_traceTick % 10) == 0)
            Logger.Log($"[Trace] pw={pw} ph={ph} ratio={AnchorRatioOnWindow:F4}"
                     + $" ground={groundPx:F2} bounds={Bounds.Width:F2}x{Bounds.Height:F2}"
                     + $" scale={Scale:F3} dpi={_cachedScaling:F2}");

        unsafe
        {
            using var fb = _wbmp.Lock();
            byte* dst = (byte*)fb.Address;
            byte* src = (byte*)pixmap.GetPixels();
            int srcRow = pixmap.RowBytes;
            int dstRow = fb.RowBytes;
            int copyLen = Math.Min(srcRow, dstRow);
            for (int y = 0; y < ph; y++)
                Buffer.MemoryCopy(src + (long)y * srcRow, dst + (long)y * dstRow, dstRow, copyLen);
        }
    }

    /// <summary>
    /// 更新视框：只算半宽/半高，中心固定为静态包围盒中心。
    ///
    /// 只用「模型顶点」计算，绝不参考窗口尺寸 —— 否则会形成
    /// 「窗口变大 → 比例变大 → 视框变大 → 窗口更大」的反馈环。
    ///
    /// 扩张立即生效、收缩才平滑：动作起手那几帧必须马上让开，
    /// 否则窗口来不及变大，角色当场被裁。
    /// </summary>
    private void UpdateView()
    {
        var sb = StaticBox;
        float ax = _model.AnchorX;
        float ay = _model.AnchorY;

        // 半宽相对「锚点」算 —— 锚点在水平中心，左右各占一半；取两侧较大值保证对称。
        // 半高只算「向上」：锚点（脚底）钉在窗口下沿上方，角色只往上长，
        // 下方那一点点（脚下特效）由 GroundInset 兜住。
        float statHalfW = Math.Max(1e-3f, Math.Max(ax - sb.MinX, sb.MaxX - ax));
        float statHalfH = Math.Max(1e-3f, sb.MaxY - ay);

        float targetW = statHalfW;
        float targetH = statHalfH;

        if (IsDynamicMode && DynamicView && TryGetFrameBounds(out var fb))
        {
            // 当前帧相对中心需要多大的半宽/半高。
            // 取两侧的较大值 —— 对称扩张才不会让角色偏离窗口中心。
            float needW = Math.Max(ax - fb.MinX, fb.MaxX - ax);
            float needH = fb.MaxY - ay;

            if (needW > targetW) targetW = needW;
            if (needH > targetH) targetH = needH;

            // 天花板有两层：
            //   ① 绝对包围盒 —— 所有帧的顶点并集，扩到它就一定不会裁掉任何一帧
            //      （所以讣告人 skill3 那种「手甩出很远」的动作也能完整显示）；
            //   ② 静态框的 MaxExpand 倍 —— 安全阀，防止 born 这类「从画面外飞入」
            //      的位移动画把窗口瞬间撑成半个屏幕。
            float capW = Math.Min(statHalfW * MaxExpand, Math.Max(statHalfW, AbsHalfW(ax)));
            float capH = Math.Min(statHalfH * MaxExpand, Math.Max(statHalfH, AbsHalfH(ay)));

            targetW = Math.Min(targetW, capW);
            targetH = Math.Min(targetH, capH);
        }

        // 不能比静态框更小，否则角色会被整体缩小
        if (targetW < statHalfW) targetW = statHalfW;
        if (targetH < statHalfH) targetH = statHalfH;

        // 扩张立即生效；收缩延迟 1 秒，然后「一次到位」。
        //
        // 之前收缩是按 12%/帧插值的，等于在收缩的那 0.6 秒里让 DesiredPetSize
        // 每帧都变小一点点，OnSizeTick 每 3 DIP 就跟一次 resize —— 每次 resize 都要
        // UpdateLayout + 改 Position，窗口就一格一格地抖。
        // 直接赋值只会产生 1 次 resize，代价是窗口"啪"地收回去，但那一瞬间角色
        // 本来也已经在回待机，肉眼几乎看不出。
        float now = (float)_clock.Elapsed.TotalSeconds;
        const float ShrinkDelay = 1.0f;

        if (targetW > _halfW) { _halfW = targetW; _lastGrowTime = now; }
        else if (targetW < _halfW && now - _lastGrowTime > ShrinkDelay) _halfW = targetW;

        if (targetH > _halfH) { _halfH = targetH; _lastGrowTime = now; }
        else if (targetH < _halfH && now - _lastGrowTime > ShrinkDelay) _halfH = targetH;
    }

    /// <summary>锚点到绝对包围盒左/右边界中较远的那个距离。</summary>
    private float AbsHalfW(float anchorX)
    {
        var b = _model.AbsBounds;
        return Math.Max(anchorX - b.MinX, b.MaxX - anchorX);
    }

    /// <summary>锚点到绝对包围盒上边界的距离（窗口高度只需要容纳这一侧）。</summary>
    private float AbsHalfH(float anchorY) => _model.AbsBounds.MaxY - anchorY;

    /// <summary>把视框立刻复位到静态包围盒（关闭动态尺寸时用）。</summary>
    public void ResetView()
    {
        var b = StaticBox;
        _halfW = Math.Max(1e-3f, Math.Max(_model.AnchorX - b.MinX, b.MaxX - _model.AnchorX));
        _halfH = Math.Max(1e-3f, b.MaxY - _model.AnchorY);
    }

    /// <summary>
    /// 取当前帧实际顶点包围盒（Spine 本地坐标）。
    /// 动作幅度大时会比静态 Bounds 大，动态窗口就是靠它。
    /// </summary>
    private bool TryGetFrameBounds(out (float MinX, float MinY, float MaxX, float MaxY) bounds)
    {
        bounds = default;
        if (_disposed) return false;

        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;

        foreach (var slot in _model.Skeleton.DrawOrder)
        {
            if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

            int need = va.WorldVerticesLength;
            if (_boundsBuf.Length < need) _boundsBuf = new float[need];

            va.ComputeWorldVertices(slot, _boundsBuf);
            for (int i = 0; i + 1 < need; i += 2)
            {
                if (_boundsBuf[i] < x0) x0 = _boundsBuf[i];
                if (_boundsBuf[i] > x1) x1 = _boundsBuf[i];
                if (_boundsBuf[i + 1] < y0) y0 = _boundsBuf[i + 1];
                if (_boundsBuf[i + 1] > y1) y1 = _boundsBuf[i + 1];
            }
        }

        if (x0 > x1 || y0 > y1) return false;

        bounds = (x0, y0, x1, y1);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var img = (IImage?)_wbmp;
        if (img != null)
            context.DrawImage(img, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _renderer.Dispose();
        _surface?.Dispose();
        _wbmp?.Dispose();
    }
}

/// <summary>窗口取框模式的取值（配置里存的就是这几个字符串）。</summary>
public static class FrameModes
{
    /// <summary>动作范围框：固定尺寸，覆盖所有原地动作（默认）。</summary>
    public const string Motion = "motion";

    /// <summary>纯绝对框：把「角色飞出画面」的位移帧也算进去，窗口更宽。</summary>
    public const string Absolute = "absolute";

    /// <summary>分位框 + 随动作伸缩（旧行为）。</summary>
    public const string Dynamic = "dynamic";

    public static string Normalize(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        Motion => Motion,
        Dynamic => Dynamic,
        _ => Absolute,     // 没配置 / 配置写错 → 用「绝不裁」的绝对框
    };

    public static string Label(string mode) => Normalize(mode) switch
    {
        Motion => "动作范围（固定，略小）",
        Dynamic => "随动作伸缩（最小，会变尺寸）",
        _ => "绝对框（最大，绝不裁）",
    };
}
