using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CcfReminderTray;

/// <summary>
/// 运行时绘制一个简洁的时钟图标（蓝底白针），避免引入二进制资源文件。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);

    /// <summary>创建图标，并返回需要随进程生命周期管理的原生句柄。</summary>
    public static (Icon Icon, nint Handle) CreateClockIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var face = new Rectangle(2, 2, size - 4, size - 4);
            using var bg = new SolidBrush(Color.FromArgb(30, 90, 168));
            using var ring = new Pen(Color.White, 2f);
            g.FillEllipse(bg, face);
            g.DrawEllipse(ring, face);

            using var hand = new Pen(Color.White, 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            const float cx = size / 2f;
            const float cy = size / 2f;
            g.DrawLine(hand, cx, cy, cx, cy - 8);     // 分针指向 12 点
            g.DrawLine(hand, cx, cy, cx + 6, cy + 4); // 时针指向约 4 点
        }

        nint handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    /// <summary>释放 Icon 及其原生句柄（GetHicon 得到的句柄不会被 Icon.Dispose 释放）。</summary>
    public static void Destroy(Icon icon, nint handle)
    {
        icon.Dispose();
        if (handle != nint.Zero)
        {
            DestroyIcon(handle);
        }
    }
}
