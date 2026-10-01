using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace 播放器.Core
{
    /// <summary>
    /// 设置方案里可以单独挑着载入的一组设置。
    /// <para>
    /// 分组按"用户心里的功能块"划，不按代码里的字段顺序：一份方案里既有音量也有桌面歌词，
    /// 用户想要的多半是"把这套外观拿过来、但音量别动"，所以载入时要能一组一组地勾。
    /// </para>
    /// </summary>
    public enum SettingsSection
    {
        /// <summary>音量 / 静音 / 速度 / 循环 / 随机 / 画面比例。</summary>
        Playback,

        /// <summary>主题、播放列表与侧栏的显示、窗口置顶、托盘、排序方式。</summary>
        Interface,

        /// <summary>主窗口与悬浮窗的位置大小、分栏宽度、吸附。</summary>
        Windows,

        /// <summary>歌词字体、在线歌词与封面（含服务地址与密钥）、桌面歌词的外观与位置。</summary>
        Lyrics,

        /// <summary>均衡器、音频输出设备、去隔行。</summary>
        Output,

        /// <summary>记住播放进度、全局媒体键、诊断日志。</summary>
        Behavior,

        /// <summary>上次打开目录 / 上次的播放列表 / 当前歌单 / 当前方案。</summary>
        Session
    }

    /// <summary>
    /// 一份设置方案的内容规则：装哪几组、一组是哪几项、按组把方案并进当前设置。
    /// <para>
    /// <b>为什么要按名字列出来而不是照抄 60 个属性赋值</b>：赋值会漏（漏一个没人发现，
    /// 用户只觉得"载入了但那一项没变"）。这里把每组的字段名写死成清单，
    /// 再用 <see cref="UncoveredProperties"/> 对着真的设置文件核一遍"有没有哪一项不属于任何组"，
    /// 冒烟测试会把这条钉住——加了新设置却忘了归类，测试立刻红。
    /// </para>
    /// <para>
    /// 合并走 JSON 而不是反射：设置本来就能序列化成 JSON，
    /// 拿方案里那几组的键覆盖当前设置的键、再反序列化回来，
    /// 既不用反射，也不用给每个属性写一遍 if。
    /// </para>
    /// </summary>
    public static class SettingsProfile
    {
        /// <summary>全部分组，按界面上从上到下的顺序。</summary>
        public static readonly IReadOnlyList<SettingsSection> All = new[]
        {
            SettingsSection.Playback,
            SettingsSection.Interface,
            SettingsSection.Windows,
            SettingsSection.Lyrics,
            SettingsSection.Output,
            SettingsSection.Behavior,
            SettingsSection.Session
        };

        /// <summary>每一组装哪些设置项（名字必须和 settings.json 里的键一模一样）。</summary>
        private static readonly Dictionary<SettingsSection, string[]> Fields = new Dictionary<SettingsSection, string[]>
        {
            [SettingsSection.Playback] = new[]
            {
                "Volume", "Muted", "Rate", "RepeatMode", "Shuffle", "AspectRatio"
            },
            [SettingsSection.Interface] = new[]
            {
                "Theme", "ShowPlaylist", "ShowSidebar", "SidebarWidth", "SidebarPage",
                "SidebarFloating", "PlaylistFloating", "AlwaysOnTop", "MinimizeToTray",
                "SortField", "SortDescending"
            },
            [SettingsSection.Windows] = new[]
            {
                "WindowX", "WindowY", "WindowWidth", "WindowHeight", "WindowMaximized", "SplitterDistance",
                "SidebarWindowX", "SidebarWindowY", "SidebarWindowWidth", "SidebarWindowHeight",
                "SidebarSnap", "SidebarSnapOffset",
                "PlaylistWindowX", "PlaylistWindowY", "PlaylistWindowWidth", "PlaylistWindowHeight",
                "PlaylistSnap", "PlaylistSnapOffset",
                "DesktopLyricsX", "DesktopLyricsY", "DesktopLyricsPlaced"
            },
            [SettingsSection.Lyrics] = new[]
            {
                "LyricsFontFamily",
                "DesktopLyricsEnabled", "DesktopLyricsFontSize", "DesktopLyricsOpacity",
                "DesktopLyricsLocked", "DesktopLyricsShowNext", "DesktopLyricsColor",
                "OnlineLookup", "LrcApiBaseUrl", "LrcApiToken"
            },
            [SettingsSection.Output] = new[]
            {
                "Equalizer", "Deinterlace", "AudioDeviceId", "AudioDeviceModule"
            },
            [SettingsSection.Behavior] = new[]
            {
                "ResumePlayback", "GlobalMediaKeys", "DiagnosticLog"
            },
            [SettingsSection.Session] = new[]
            {
                "LastDirectory", "CurrentPlaylistName", "CurrentSettingsProfileName",
                "LastPlaylist", "LastPlaylistIndex"
            }
        };

        /// <summary>一组设置的名字（菜单 / 勾选框里显示的那个）。</summary>
        public static string Name(SettingsSection section) => section switch
        {
            SettingsSection.Playback => "播放",
            SettingsSection.Interface => "界面与主题",
            SettingsSection.Windows => "窗口与悬浮",
            SettingsSection.Lyrics => "歌词与桌面歌词",
            SettingsSection.Output => "音视频输出",
            SettingsSection.Behavior => "行为",
            SettingsSection.Session => "会话",
            _ => section.ToString()
        };

        /// <summary>一句话说清这一组装了什么（勾选框上的说明）。</summary>
        public static string Hint(SettingsSection section) => section switch
        {
            SettingsSection.Playback => "音量、静音、播放速度、循环、随机、画面比例",
            SettingsSection.Interface => "主题、播放列表与侧栏、窗口置顶、托盘、排序方式",
            SettingsSection.Windows => "窗口位置与大小、分栏宽度、悬浮窗的位置与吸附、桌面歌词的位置",
            SettingsSection.Lyrics => "歌词字体、在线歌词与封面（含服务地址与密钥）、桌面歌词的外观",
            SettingsSection.Output => "均衡器、音频输出设备、去隔行",
            SettingsSection.Behavior => "记住播放进度、全局媒体键、诊断日志",
            SettingsSection.Session => "上次打开目录、上次的播放列表、当前歌单、当前方案",
            _ => string.Empty
        };

        /// <summary>这一组包含的设置项名字。</summary>
        public static IReadOnlyList<string> Properties(SettingsSection section) =>
            Fields.TryGetValue(section, out var names) ? names : Array.Empty<string>();

        /// <summary>
        /// 没有归进任何一组的设置项（正常应当是空的）。
        /// <para>加了新设置却忘了归类时，它就会冒出来——冒烟测试拿它当断言。</para>
        /// </summary>
        public static IReadOnlyList<string> UncoveredProperties()
        {
            var covered = new HashSet<string>(Fields.Values.SelectMany(names => names), StringComparer.Ordinal);

            return KeysOf(new AppSettings())
                .Where(key => !covered.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>组里写了、但设置里根本没有的名字（拼错时冒出来，同样是给测试用的）。</summary>
        public static IReadOnlyList<string> UnknownProperties()
        {
            var existing = new HashSet<string>(KeysOf(new AppSettings()), StringComparer.Ordinal);

            return Fields.Values
                .SelectMany(names => names)
                .Where(name => !existing.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 把 <paramref name="incoming"/>（一份方案）里被选中的那几组，覆盖到
        /// <paramref name="target"/>（当前设置）上，返回一份新的设置。
        /// <para>没被选中的组一律保持 <paramref name="target"/> 的原样——"只把这些拿过来"就是这个意思。</para>
        /// </summary>
        public static AppSettings Merge(
            AppSettings target, AppSettings incoming, IEnumerable<SettingsSection> sections)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));
            if (sections == null) throw new ArgumentNullException(nameof(sections));

            var merged = JsonNode.Parse(target.ToJson())?.AsObject();
            var source = JsonNode.Parse(incoming.ToJson())?.AsObject();

            if (merged == null || source == null) return target;

            foreach (var section in sections)
            {
                foreach (var name in Properties(section))
                {
                    if (!source.TryGetPropertyValue(name, out var value)) continue;

                    merged[name] = value?.DeepClone();
                }
            }

            // 反序列化回来会顺带走一遍"把可空字段补回默认值"，所以方案文件被手改坏了也不会当场炸。
            return AppSettings.TryFromJson(merged.ToJsonString(), out var result) ? result : target;
        }

        /// <summary>这一组里，方案和当前设置有<b>几项不一样</b>（勾选框上写出来，点之前就知道有没有区别）。</summary>
        public static int CountDifferences(AppSettings current, AppSettings incoming, SettingsSection section)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));

            var mine = JsonNode.Parse(current.ToJson())?.AsObject();
            var theirs = JsonNode.Parse(incoming.ToJson())?.AsObject();

            if (mine == null || theirs == null) return 0;

            var differences = 0;

            foreach (var name in Properties(section))
            {
                var left = mine.TryGetPropertyValue(name, out var a) ? a?.ToJsonString() : null;
                var right = theirs.TryGetPropertyValue(name, out var b) ? b?.ToJsonString() : null;

                if (!string.Equals(left, right, StringComparison.Ordinal)) differences++;
            }

            return differences;
        }

        private static IEnumerable<string> KeysOf(AppSettings settings) =>
            (JsonNode.Parse(settings.ToJson())?.AsObject() ?? new JsonObject())
            .Select(pair => pair.Key);
    }
}
