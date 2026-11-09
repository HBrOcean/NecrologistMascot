using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using NecrologistMascot.Services;
using NecrologistMascot.Rendering;
using Spine;

namespace NecrologistMascot;

/// <summary>
/// 诊断：绕开 Avalonia，直接用 SkiaSharp 把角色渲染成 PNG，并打印附件 / 旋转 / 贴图信息。
///
/// 用法：
///   NecrologistMascot.exe --dump            渲染所有可用皮肤
///   NecrologistMascot.exe --dump 303701     只渲染指定皮肤
///
/// 输出目录：系统临时目录下的 NecrologistMascot-dump/
/// </summary>
public static class Diagnostics
{
    public static void Dump(string? onlySkinId = null)
    {
        // 不写死皮肤 id：一切以 Assets/characters 的扫描结果为准
        var skins = new List<SkinEntry>();
        foreach (var e in SkinCatalog.All)
        {
            if (!e.IsUsable) continue;
            if (!string.IsNullOrWhiteSpace(onlySkinId)
                && !string.Equals(e.Id, onlySkinId, StringComparison.OrdinalIgnoreCase)) continue;
            skins.Add(e);
        }

        if (skins.Count == 0)
        {
            Console.WriteLine("[Dump] 没有可用的皮肤，先确认 Assets/characters 下有 .atlas/.skel");
            return;
        }

        string outDir = Path.Combine(Path.GetTempPath(), "NecrologistMascot-dump");
        Directory.CreateDirectory(outDir);

        foreach (var skin in skins)
        {
            try { DumpOne(skin, outDir); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dump] {skin.Id} 失败：{ex}");
            }
        }

        Console.WriteLine("[Dump] 输出目录 " + outDir);
    }

    private static void DumpOne(SkinEntry skin, string outDir)
    {
        string dir = skin.Dir!;
        string id = skin.Id;
        Console.WriteLine($"\n===== {id} ({skin.DisplayName}) =====");
        Console.WriteLine("[Dump] atlas 目录: " + dir);

        using var model = new SkeletonModel(
            Path.Combine(dir, id + ".atlas"),
            Path.Combine(dir, id + ".skel"));

        Console.WriteLine("[Dump] 动画: " + string.Join(", ", model.AnimationNames));
        var b = model.Bounds;
        Console.WriteLine($"[Dump] Bounds: {b.MinX:F1},{b.MinY:F1} .. {b.MaxX:F1},{b.MaxY:F1}"
                        + $"  锚点 ({model.AnchorX:F1},{model.AnchorY:F1})");

        // 用待机动画推进几帧再统计：setup pose 里大量 slot 的 attachment 还是 null
        model.SetAnimation(0, model.HasAnimation("idle") ? "idle" : "", true);
        model.Update(0.5f);

        int regionCount = 0, meshCount = 0, rotCount = 0, seqCount = 0;
        foreach (var slot in model.Skeleton.DrawOrder)
        {
            var att = slot.Attachment;
            if (att == null) continue;
            if (att is RegionAttachment r)
            {
                regionCount++;
                var ar = r.Region as AtlasRegion;
                bool seq = r.Sequence != null;
                if (seq) seqCount++;
                if (ar != null && ar.degrees != 0) rotCount++;
                Console.WriteLine($"[Region] {att.Name,-28} deg={ar?.degrees,-3} seq={seq} "
                                + $"uvs=[{MinV(r.UVs):F3},{MaxV(r.UVs):F3}] region={ar?.name} page={(ar?.page != null)}");
            }
            else if (att is MeshAttachment m)
            {
                meshCount++;
                var ar = m.Region as AtlasRegion;
                bool seq = m.Sequence != null;
                if (seq) seqCount++;
                if (ar != null && ar.degrees != 0) rotCount++;
                var uvs = m.UVs;
                Console.WriteLine($"[Mesh]   {att.Name,-28} v={m.WorldVerticesLength / 2,-4} deg={ar?.degrees,-3} seq={seq} "
                                + $"uvs=[{MinV(uvs):F3},{MaxV(uvs):F3}] region={ar?.name}");
            }
        }
        Console.WriteLine($"[Dump] Region={regionCount} Mesh={meshCount} 旋转={rotCount} sequence={seqCount}");

        // 两张：一张浅蓝灰底（方便看灰白块/镂空），一张全透明（方便看 alpha 边缘）
        WritePng(model, b, Path.Combine(outDir, id + "-solid.png"), new SKColor(200, 210, 225));
        WritePng(model, b, Path.Combine(outDir, id + "-alpha.png"), SKColors.Transparent);
    }

    private static void WritePng(SkeletonModel model, (float MinX, float MinY, float MaxX, float MaxY) b,
                                 string outPath, SKColor background)
    {
        int W = 700, H = 900;
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(background);

        float cx = (b.MinX + b.MaxX) / 2f, cy = (b.MinY + b.MaxY) / 2f;
        float bw = Math.Max(1f, b.MaxX - b.MinX), bh = Math.Max(1f, b.MaxY - b.MinY);
        float scale = Math.Min(W / bw, H / bh) * 0.9f;

        canvas.Save();
        canvas.Translate(W / 2f, H / 2f);
        canvas.Scale(scale, -scale);
        canvas.Translate(-cx, -cy);
        using (var renderer = new SpineCanvasRenderer())
            renderer.Draw(canvas, model.Skeleton, model);
        canvas.Restore();
        canvas.Flush();

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outPath, data.ToArray());
        Console.WriteLine("[Dump] 已保存 " + outPath);
    }

    /// <summary>
    /// 几何自检：不启动窗口，纯数值模拟「窗口随动作自适应」的视框，
    /// 逐动画逐帧算出角色是否会画到窗口外。
    ///
    /// 越界判定用比例表示：1.00 = 刚好贴边，&gt; 1 表示超出。
    /// 视框规则与 PetRenderControl.UpdateView 完全一致（扩张立即生效、上限 1.5 倍）。
    /// </summary>
    public static void Fit(double scale = 0.15, string? onlySkinId = null)
    {
        const double PadX = PetRenderControl.PadX;
        const double PadY = PetRenderControl.PadY;
        const double GroundInset = PetRenderControl.GroundInset;
        const float MaxExpand = PetRenderControl.MaxExpand;

        Console.WriteLine($"[Fit] 缩放 {scale:P0}，PadX {PadX:P0}，PadY {PadY:P0}，GroundInset {GroundInset} DIP");

        foreach (var skin in SkinCatalog.All)
        {
            if (!skin.IsUsable) continue;
            if (!string.IsNullOrWhiteSpace(onlySkinId)
                && !string.Equals(skin.Id, onlySkinId, StringComparison.OrdinalIgnoreCase)) continue;

            using var model = new SkeletonModel(
                Path.Combine(skin.Dir!, skin.Id + ".atlas"),
                Path.Combine(skin.Dir!, skin.Id + ".skel"));

            var b = model.Bounds;
            float ax = model.AnchorX, ay = model.AnchorY;
            float statHalfW = Math.Max(1e-3f, Math.Max(ax - b.MinX, b.MaxX - ax));
            float statHalfH = Math.Max(1e-3f, b.MaxY - ay);   // 只算向上（锚点在下沿）

            var abs = model.AbsBounds;
            float absHalfW = Math.Max(ax - abs.MinX, abs.MaxX - ax);
            float absHalfH = abs.MaxY - ay;
            float groundDips = (float)Math.Max(PetRenderControl.GroundInset,
                                               (ay - b.MinY) * scale + 4);

            float worstW = 0, worstUp = 0, worstDown = 0;
            string worstWAnim = "", worstUpAnim = "", worstDownAnim = "";

            var buf = new float[512];
            foreach (var anim in model.Data.Animations)
            {
                float dur = anim.Duration <= 0 ? 0.1f : anim.Duration;
                const int steps = 48;
                for (int i = 0; i <= steps; i++)
                {
                    anim.Apply(model.Skeleton, 0f, dur * i / steps, false, null, 1f, MixBlend.Setup, MixDirection.In);
                    model.Skeleton.UpdateWorldTransform(Skeleton.Physics.None);

                    float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                    foreach (var slot in model.Skeleton.DrawOrder)
                    {
                        if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;
                        if (buf.Length < va.WorldVerticesLength) buf = new float[va.WorldVerticesLength];
                        va.ComputeWorldVertices(slot, buf);
                        for (int k = 0; k + 1 < va.WorldVerticesLength; k += 2)
                        {
                            if (buf[k] < x0) x0 = buf[k];
                            if (buf[k] > x1) x1 = buf[k];
                            if (buf[k + 1] < y0) y0 = buf[k + 1];
                            if (buf[k + 1] > y1) y1 = buf[k + 1];
                        }
                    }
                    if (x0 > x1) continue;

                    float needW = Math.Max(ax - x0, x1 - ax);
                    float needUp = y1 - ay;
                    float needDown = ay - y0;

                    // 与 UpdateView 同一套规则（含两层天花板：绝对框 + MaxExpand 安全阀）
                    float capW = Math.Min(statHalfW * MaxExpand, Math.Max(statHalfW, absHalfW));
                    float capH = Math.Min(statHalfH * MaxExpand, Math.Max(statHalfH, absHalfH));
                    float viewW = Math.Min(Math.Max(statHalfW, needW), capW);
                    float viewH = Math.Min(Math.Max(statHalfH, needUp), capH);

                    float availW = viewW * (float)(1 + PadX);
                    float availUp = viewH * (float)(1 + PadY);
                    float availDown = (float)(groundDips / Math.Max(1e-6, scale));   // DIP → Spine 单位

                    float rW = needW / Math.Max(1e-3f, availW);
                    float rUp = needUp / Math.Max(1e-3f, availUp);
                    float rDown = needDown / Math.Max(1e-3f, availDown);

                    if (rW > worstW) { worstW = rW; worstWAnim = anim.Name; }
                    if (rUp > worstUp) { worstUp = rUp; worstUpAnim = anim.Name; }
                    if (rDown > worstDown) { worstDown = rDown; worstDownAnim = anim.Name; }
                }
            }

            Console.WriteLine($"[Fit] {skin.Id}（{skin.DisplayName}）静态半框 {statHalfW:F0}x{statHalfH:F0}"
                            + $"，绝对半框 {absHalfW:F0}x{absHalfH:F0}，锚点 ({ax:F1},{ay:F1})");
            Console.WriteLine($"[Fit]   横向最坏 {worstW:P0}（{worstWAnim}）  "
                            + $"向上最坏 {worstUp:P0}（{worstUpAnim}）  "
                            + $"向下最坏 {worstDown:P0}（{worstDownAnim}）");
            Console.WriteLine(worstW <= 1 && worstUp <= 1 && worstDown <= 1
                ? "[Fit]   ✅ 所有帧都在窗口内"
                : "[Fit]   ⚠ 有帧会越界（>100%）");
        }
    }

    private static float MinV(float[] a)
    {
        float m = float.MaxValue;
        foreach (var x in a) if (x < m) m = x;
        return m;
    }    private static float MaxV(float[] a)
    {
        float m = float.MinValue;
        foreach (var x in a) if (x > m) m = x;
        return m;
    }
}
