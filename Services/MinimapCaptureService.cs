using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 游戏内小地图截取（精确优先）。
///
/// 三条硬性要求（按用户明确要求实现）：
///  1) 放大后自动继续：先把小地图按 "+" 放大到最大档 → 截图 → 按 "-" 完整缩回，
///     全程异常也保证走恢复分支，绝不把游戏卡在放大态。
///  2) 精确：
///     - 区域按「游戏窗口锚点」换算，窗口移动/缩放后自动跟着走，不会截偏；
///     - 连续取多帧，用亮度标准差挑最"实"的一帧，避免截到过渡/黑帧；
///     - 质量闸门：画面近似纯色（黑屏/截到空白处）判为失败并重试。
///  3) 按住 Alt 截取：游戏内按住 Alt 会在小地图上叠加舰名/血量/航向等扩展信息，
///     AI 更容易读。Alt 在截图前按下、截图后立刻抬起，异常路径也会抬起。
///
/// 输入必须用 SendInput 扫描码（游戏走 RawInput，PostMessage 无效，见 GameInputHelper 注释）。
/// </summary>
public static class MinimapCaptureService
{
    /// <summary>截取参数</summary>
    public sealed class Options
    {
        /// <summary>截取时按住 Alt（显示舰名/血量）。默认开。</summary>
        public bool HoldAlt { get; set; } = true;

        /// <summary>先把小地图放大到最大再截（截完自动缩回）。默认关：直接截渲染结果最稳。</summary>
        public bool ZoomToMax { get; set; } = false;

        /// <summary>放大/缩小各按几次（到顶/到底即停，多按无害）。</summary>
        public int ZoomPresses { get; set; } = 8;

        /// <summary>按下 Alt 后等游戏渲染出扩展信息的毫秒数。</summary>
        public int AltSettleMs { get; set; } = 260;

        /// <summary>每次按键后的间隔。</summary>
        public int PressDelayMs { get; set; } = 60;

        /// <summary>放大完成后的渲染等待。</summary>
        public int RenderWaitMs { get; set; } = 420;

        /// <summary>取几帧挑最好的一帧。</summary>
        public int Frames { get; set; } = 3;

        /// <summary>帧间隔。</summary>
        public int FrameGapMs { get; set; } = 90;

        /// <summary>亮度标准差下限，低于此值视为"空帧"（黑屏/截错区域）。</summary>
        public double MinStdDev { get; set; } = 6.0;

        /// <summary>失败重试次数。</summary>
        public int Retries { get; set; } = 2;
    }

    /// <summary>截取结果 + 诊断信息</summary>
    public sealed class Result
    {
        public BitmapSource? Image { get; set; }
        public bool UsedAlt { get; set; }
        public bool UsedZoom { get; set; }
        public bool UsedAnchor { get; set; }
        public Rect Region { get; set; }
        /// <summary>选中帧的亮度标准差，越大说明画面越"有内容"。</summary>
        public double Sharpness { get; set; }
        public string? Warning { get; set; }
        public bool Success => Image != null;
    }

    /// <summary>
    /// 把「校准时保存的区域」按当前游戏窗口位置换算成屏幕坐标。
    /// 只移动 → 平移；改变大小 → 按比例缩放。没有锚点信息时原样返回。
    /// </summary>
    public static Rect ResolveRegion(Rect saved, Rect anchor, Rect current)
    {
        if (saved.IsEmpty || saved.Width <= 0) return saved;
        if (anchor.IsEmpty || current.IsEmpty || anchor.Width <= 0 || anchor.Height <= 0)
            return saved;

        double dw = current.Width - anchor.Width;
        double dh = current.Height - anchor.Height;

        if (Math.Abs(dw) <= 2 && Math.Abs(dh) <= 2)
        {
            // 仅平移
            return new Rect(saved.X + (current.X - anchor.X),
                            saved.Y + (current.Y - anchor.Y),
                            saved.Width, saved.Height);
        }

        // 尺寸变了：按窗口缩放比例换算
        double sx = current.Width / anchor.Width;
        double sy = current.Height / anchor.Height;
        return new Rect(
            current.X + (saved.X - anchor.X) * sx,
            current.Y + (saved.Y - anchor.Y) * sy,
            saved.Width * sx,
            saved.Height * sy);
    }

    /// <summary>
    /// 执行一次完整的小地图截取（放大 → 按住 Alt → 多帧挑优 → 恢复）。
    /// 调用方需自行隐藏自己的窗口（避免悬浮窗入镜）。
    /// </summary>
    public static Result Capture(Rect savedRegion, Rect anchorWindow, Options? opt = null)
    {
        opt ??= new Options();
        var result = new Result();

        var hwnd = GameInputHelper.FindGameWindow();
        if (hwnd == IntPtr.Zero)
        {
            result.Warning = "未找到游戏窗口，按原区域直接截取";
            return CapturePlain(savedRegion, opt, result);
        }

        var winRect = GameInputHelper.GetWindowScreenRect(hwnd);
        var region = ResolveRegion(savedRegion, anchorWindow, winRect);
        result.UsedAnchor = !winRect.IsEmpty && !anchorWindow.IsEmpty;
        result.Region = region;

        if (!GameInputHelper.ActivateGameWindow(hwnd))
        {
            result.Warning = "游戏窗口无法激活，按原区域直接截取";
            return CapturePlain(savedRegion, opt, result);
        }

        for (int attempt = 0; attempt <= opt.Retries; attempt++)
        {
            var shot = CaptureOnce(hwnd, region, opt, result);
            if (shot != null)
            {
                result.Image = shot.Item1;
                result.Sharpness = shot.Item2;
                return result;
            }
            if (attempt < opt.Retries)
            {
                AppLog.Warn($"小地图截取第 {attempt + 1} 次为空帧，重试（region={region}）");
                Thread.Sleep(200);
            }
        }

        result.Warning = "多次截取均为空帧，请检查小地图区域是否仍然对准";
        return result;
    }

    // ---- 内部实现 ----

    private static Result CapturePlain(Rect region, Options opt, Result result)
    {
        result.Region = region;
        try
        {
            var bmp = GrabBestFrame(region, opt);
            if (bmp != null)
            {
                result.Image = bmp.Item1;
                result.Sharpness = bmp.Item2;
            }
        }
        catch (Exception ex)
        {
            result.Warning = "直接截取失败：" + ex.Message;
        }
        return result;
    }

    private static Tuple<BitmapSource, double>? CaptureOnce(
        IntPtr hwnd, Rect region, Options opt, Result result)
    {
        bool zoomed = false, altDown = false;
        try
        {
            if (opt.ZoomToMax)
            {
                for (int i = 0; i < opt.ZoomPresses; i++)
                {
                    GameInputHelper.SendPlus(hwnd);
                    Thread.Sleep(opt.PressDelayMs);
                }
                zoomed = true;
                result.UsedZoom = true;
                Thread.Sleep(opt.RenderWaitMs);
            }

            if (opt.HoldAlt)
            {
                GameInputHelper.HoldAlt();
                altDown = true;
                result.UsedAlt = true;
                Thread.Sleep(opt.AltSettleMs);
            }

            return GrabBestFrame(region, opt);
        }
        catch (Exception ex)
        {
            AppLog.Warn("小地图截取过程异常: " + ex.Message);
            return null;
        }
        finally
        {
            // 恢复顺序：先松 Alt（避免放大态下 Alt 组合键误触发），再缩回小地图
            if (altDown)
            {
                GameInputHelper.ReleaseAlt();
                Thread.Sleep(60);
            }
            if (zoomed)
            {
                for (int i = 0; i < opt.ZoomPresses + 2; i++)
                {
                    GameInputHelper.SendMinus(hwnd);
                    Thread.Sleep(opt.PressDelayMs);
                }
            }
            GameInputHelper.ReleaseAll();
        }
    }

    /// <summary>连拍若干帧，返回亮度标准差最大的一帧（画面最"实"）。</summary>
    private static Tuple<BitmapSource, double>? GrabBestFrame(Rect region, Options opt)
    {
        if (region.IsEmpty || region.Width < 4 || region.Height < 4) return null;

        BitmapSource? best = null;
        double bestScore = -1;
        int frames = Math.Max(1, opt.Frames);

        for (int i = 0; i < frames; i++)
        {
            var src = ScreenCaptureService.CaptureRegion(region);
            if (src == null) continue;

            double score = StdDevOf(src);
            if (score > bestScore)
            {
                bestScore = score;
                best = src;
            }
            if (i < frames - 1) Thread.Sleep(opt.FrameGapMs);
        }

        if (best == null) return null;
        if (bestScore < opt.MinStdDev) return null;   // 空帧，交给上层重试
        return Tuple.Create(best, bestScore);
    }

    /// <summary>算一张图的灰度标准差：纯色画面（黑屏/截错区域）接近 0。</summary>
    public static double StdDevOf(BitmapSource src)
    {
        try
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            src.CopyPixels(pixels, stride, 0);

            const int step = 4;                 // 抽样，够用且快
            long sum = 0, sum2 = 0, n = 0;
            for (int y = 0; y < h; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int i = row + x * 4;
                    if (i + 2 >= pixels.Length) continue;
                    int lum = (pixels[i] * 299 + pixels[i + 1] * 587 + pixels[i + 2] * 114) / 1000;
                    sum += lum; sum2 += (long)lum * lum; n++;
                }
            }
            if (n < 8) return 0;
            double mean = sum / (double)n;
            double var = sum2 / (double)n - mean * mean;
            return var > 0 ? Math.Sqrt(var) : 0;
        }
        catch { return 0; }
    }

    /// <summary>把 BitmapSource 转成 System.Drawing.Bitmap（供需要 GDI 的场合）。</summary>
    public static Bitmap ToBitmap(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[w * 4 * h];
            src.CopyPixels(pixels, w * 4, 0);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
