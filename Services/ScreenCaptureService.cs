using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 屏幕截图服务。按保存的设备像素区域截取小地图。
/// 坐标系：MinimapRegion 使用物理像素坐标（与 Graphics.CopyFromScreen 一致）。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>截取指定区域，返回 WPF 可显示的 BitmapSource</summary>
    public static BitmapSource CaptureRegion(Rect region)
    {
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException("小地图区域未设置，请先在设置中框选小地图位置。");

        var x = (int)Math.Round(region.X);
        var y = (int)Math.Round(region.Y);
        var w = (int)Math.Round(region.Width);
        var h = (int)Math.Round(region.Height);

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // CopyFromScreen 的源坐标是物理像素；WPF 的 SystemParameters 是逻辑像素，
            // 需按当前 DPI 换算成物理屏幕边界后求交集，防止区域部分/完全越出屏幕时报错。
            var scaleX = g.DpiX / 96.0;
            var scaleY = g.DpiY / 96.0;
            var vLeft = (int)Math.Round(SystemParameters.VirtualScreenLeft * scaleX);
            var vTop = (int)Math.Round(SystemParameters.VirtualScreenTop * scaleY);
            var vRight = (int)Math.Round((SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) * scaleX);
            var vBottom = (int)Math.Round((SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight) * scaleY);

            var srcLeft = Math.Max(x, vLeft);
            var srcTop = Math.Max(y, vTop);
            var srcRight = Math.Min(x + w, vRight);
            var srcBottom = Math.Min(y + h, vBottom);

            if (srcRight <= srcLeft || srcBottom <= srcTop)
                throw new InvalidOperationException("截图区域完全超出屏幕范围，请重新框选小地图位置。");

            // 只拷贝与屏幕可见区域相交的部分，其余区域保持透明（不影响目标尺寸）
            var clipW = srcRight - srcLeft;
            var clipH = srcBottom - srcTop;
            g.CopyFromScreen(srcLeft, srcTop, srcLeft - x, srcTop - y,
                new System.Drawing.Size(clipW, clipH), CopyPixelOperation.SourceCopy);
        }

        return ToBitmapSource(bmp);
    }

    /// <summary>把 BitmapSource 编码为 PNG 字节数组</summary>
    public static byte[] EncodeToPngBytes(BitmapSource source)
    {
        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>把 BitmapSource 编码为 Base64 字符串（用于 GLM 等需要 base64 的 API）</summary>
    public static string EncodeToBase64(BitmapSource source)
    {
        var bytes = EncodeToPngBytes(source);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Bitmap -> BitmapSource，并冻结以便跨线程使用</summary>
    private static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        var hBitmap = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    /// <summary>截图前可选：临时隐藏悬浮窗，避免它出现在小地图截图里</summary>
    public static Rect ValidateRegion(Rect region)
    {
        if (region.Width < 10 || region.Height < 10)
            throw new InvalidOperationException("小地图区域过小，请重新框选。");
        return region;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
