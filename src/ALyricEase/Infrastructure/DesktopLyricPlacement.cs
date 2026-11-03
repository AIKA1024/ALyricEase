namespace ALyricEase.Infrastructure;

/// <summary>悬浮窗口落位的纯计算(不依赖平台,便于回归)。</summary>
public static class DesktopLyricPlacement
{
    /// <summary>
    /// 把窗口顶部收进工作区:只有底部超出才上推,本来就合法的位置原样返回。
    /// <para>
    /// ⚠ 必须传真实的像素高度。曾经用固定 180 当"估计高度",结果靠屏幕底保存的位置
    /// 在下次启动时被顶到 <c>Bottom - 180</c>,用户看到的就是"拖到底了却还原不了"。
    /// </para>
    /// </summary>
    public static int ClampTop(int top, int pixelHeight, int areaTop, int areaBottom)
    {
        var maxTop = Math.Max(areaTop, areaBottom - Math.Max(0, pixelHeight));
        return Math.Min(Math.Max(top, areaTop), maxTop);
    }

    /// <summary>
    /// 高度是否可信。<c>SizeToContent</c> 的窗口在首帧之前,<c>SizeChanged</c> 会先报一次
    /// <c>(Width, Width)</c> 的假尺寸(实测 720×720),拿它算高度会把贴底的窗口顶到屏幕中间
    /// (1040 - 720 = 320)。歌词窗口不可能占到工作区一半以上,超了就当没拿到。
    /// </summary>
    public static bool IsPlausibleHeight(int pixelHeight, int areaHeight)
        => pixelHeight > 0 && pixelHeight <= areaHeight / 2;
}
