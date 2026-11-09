using System;

namespace 播放器.Core
{
    /// <summary>DVD 菜单里的一次操作（值就是 libvlc 的 <c>libvlc_navigate_mode</c>）。</summary>
    public enum DvdMenuAction
    {
        /// <summary>确定 / 进入（libvlc 的 activate）。</summary>
        Activate = 0,

        Up = 1,

        Down = 2,

        Left = 3,

        Right = 4,

        /// <summary>回到上一级 / 菜单根（libvlc 的 popup）。</summary>
        Popup = 5
    }

    /// <summary>
    /// 光盘菜单的判定与文案（纯逻辑：不碰 libvlc，也不碰 WinForms）。
    /// <para>
    /// 为什么单独抽出来：菜单导航最怕"看着像菜单、其实在放正片"，或者反过来。
    /// 把"这是不是菜单"写成一条能单独验的规则，比在窗体里到处写 <c>if (Title == 0)</c> 可靠；
    /// 真盘上的两个数（<c>TitleCount</c>、<c>Title</c>）也就只剩一处解释。
    /// </para>
    /// <para>
    /// 事实来自 2026-11-09 的真盘实测（见 <c>docs/1.16.0计划.md</c> 第 2 节）：
    /// <c>dvd://</c> 的 title 0 是菜单（本机那张盘 <c>TitleCount = 13</c>、菜单标题 466 ms）；
    /// <c>dvdsimple://</c> 的 title 0 就是正片（<c>TitleCount = 12</c>）。
    /// 所以"是不是菜单"必须连 MRL 一起看，不能只看标题号。
    /// </para>
    /// </summary>
    public static class DvdMenu
    {
        /// <summary>动作对应的 libvlc 导航模式值。</summary>
        public static uint ModeOf(DvdMenuAction action) => (uint)action;

        /// <summary>这个 MRL 是不是"打开菜单"的那一种（<c>dvd://</c>，不是 <c>dvdsimple://</c>）。</summary>
        public static bool IsMenuMrl(string? location)
        {
            if (string.IsNullOrWhiteSpace(location)) return false;
            if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)) return false;

            return string.Equals(uri.Scheme, "dvd", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 菜单现在是不是在屏上：<c>dvd://</c> + 有不止一个标题 + 当前停在第 0 个（菜单 / 头播放）。
        /// <para>只看这三个数，不去截屏猜：真盘上它们和画面是一一对应的。</para>
        /// </summary>
        public static bool IsOnScreen(string? location, int title, int titleCount) =>
            IsMenuMrl(location) && titleCount > 1 && title <= 0;

        /// <summary>
        /// 状态栏上的位置说明：不是 DVD 返回空串，菜单上返回"菜单"，正片返回"标题 2 / 12 · 第 3 章"。
        /// <para>标题数要减 1：<c>dvd://</c> 的 title 0 是菜单，正片是 1..N-1。</para>
        /// </summary>
        public static string DescribePosition(string? location, int title, int titleCount, int chapter)
        {
            if (!IsMenuMrl(location)) return string.Empty;
            if (titleCount <= 1) return string.Empty;

            if (title <= 0) return "菜单";

            var contentTitles = titleCount - 1;
            var text = "标题 " + Math.Min(title, contentTitles) + " / " + contentTitles;

            if (chapter >= 0) text += " · 第 " + (chapter + 1) + " 章";

            return text;
        }

        /// <summary>「标题」子菜单里那一行字。</summary>
        public static string DescribeTitle(int title, int chapterCount) =>
            chapterCount > 0 ? "标题 " + title + "（" + chapterCount + " 章）" : "标题 " + title;

        /// <summary>
        /// 菜单打不开时要不要改成"直接播放主标题"。
        /// <para>
        /// 只认三件事：当前是 <c>dvd://</c>、这个盘还没退过、错误事件真的到了。
        /// <b>不需要再等超时</b>：实测缺盘 / 坏盘时 libvlc 的错误事件 1 秒内就到，而 <c>State</c>
        /// 会停在 <c>Ended</c>——等它等到的是 25 秒的空等（探针实测，见计划文档第 2 节）。
        /// </para>
        /// </summary>
        public static bool ShouldFallBack(string? location, bool alreadyTried, bool error) =>
            error && !alreadyTried && IsMenuMrl(location);
    }
}
