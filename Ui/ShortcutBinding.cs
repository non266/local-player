using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 快捷键覆盖层（1.29.0）：设置文件里的 <c>"Id=键"</c> 条目 ↔ 生效的按键表，
    /// 以及"能不能这么改"的校验。
    /// <para>
    /// 为什么单列一个类：目录（<see cref="PlayerCommandCatalog"/>）只该管"有哪些命令、默认按什么"，
    /// 这里管"用户改成了什么、改得对不对、怎么落盘"。两边都不碰窗口，所以可以纯逻辑断言。
    /// </para>
    /// <para>
    /// 落盘只写<b>与默认不同</b>的项：没动过的命令跟着以后版本的默认键走。
    /// <c>"Id="</c>（等号后为空）表示"这条命令暂时没有键"，与"没有条目 = 用默认"是两件事。
    /// </para>
    /// </summary>
    internal static class ShortcutBinding
    {
        /// <summary>一条"固定快捷键"：不在命令目录里、也不该被命令占用的键。</summary>
        internal readonly struct ReservedShortcut
        {
            public ReservedShortcut(Keys key, string reason)
            {
                Key = key;
                Reason = reason;
            }

            public Keys Key { get; }

            public string Reason { get; }
        }

        /// <summary>
        /// 保留键表：这些键由别处处理（文件页、对话框、系统热键），
        /// 绑到命令上会被那一处先吃掉，所以一律挡在保存之前。
        /// 1.30.0 起逐帧 / 音画字幕延迟 / 歌词偏移已经进命令目录，不再列在这里。
        /// </summary>
        public static IReadOnlyList<ReservedShortcut> Reserved { get; } = new[]
        {
            new ReservedShortcut(Keys.F1, "「快捷键说明」"),
            new ReservedShortcut(Keys.Delete, "「从播放列表移除」/ 文件页里吞掉"),
            new ReservedShortcut(Keys.Escape, "退出全屏 / 关对话框"),
            new ReservedShortcut(Keys.Enter, "打开选中项 / 退出全屏"),
            new ReservedShortcut(Keys.Tab, "对话框与列表切换焦点"),
            new ReservedShortcut(Keys.Back, "文件页返回上一级"),
            new ReservedShortcut(Keys.F5, "文件页刷新"),
            new ReservedShortcut(Keys.Control | Keys.F, "文件页搜索"),
            new ReservedShortcut(Keys.Alt | Keys.Left, "文件页后退"),
            new ReservedShortcut(Keys.Alt | Keys.Right, "文件页前进"),
            new ReservedShortcut(Keys.Control | Keys.Alt | Keys.D, "桌面歌词全局锁定（系统级热键）")
        };

        /// <summary>
        /// 把设置里那一串 <c>"Id=键"</c> 解成"这条命令用哪些键"。
        /// 认不识的条目 / 键<b>不静默</b>：收进 <paramref name="warnings"/>，调用方要显示一次。
        /// </summary>
        public static Dictionary<PlayerCommandId, Keys[]> Resolve(
            IReadOnlyList<string>? entries, out List<string> warnings)
        {
            var notes = new List<string>();
            var overrides = new Dictionary<PlayerCommandId, Keys[]>();

            if (entries == null || entries.Count == 0)
            {
                warnings = notes;
                return overrides;
            }

            var requested = new Dictionary<PlayerCommandId, List<Keys>>();
            var invalid = new HashSet<PlayerCommandId>();

            foreach (var raw in entries)
            {
                var text = (raw ?? string.Empty).Trim();
                if (text.Length == 0) continue;

                var split = text.IndexOf('=');
                if (split <= 0)
                {
                    notes.Add($"认不识的快捷键条目「{text}」（没有命令名）");
                    continue;
                }

                var idText = text[..split].Trim();
                var keyText = text[(split + 1)..].Trim();

                if (idText.Length == 0 || char.IsDigit(idText[0]) ||
                    !Enum.TryParse<PlayerCommandId>(idText, ignoreCase: false, out var id) ||
                    !Enum.IsDefined(typeof(PlayerCommandId), id))
                {
                    notes.Add($"认不识的快捷键条目「{text}」（没有这条命令）");
                    continue;
                }

                if (!requested.TryGetValue(id, out var keys))
                {
                    keys = new List<Keys>();
                    requested[id] = keys;
                }

                // "Id=" = 这条命令没有键（与"没有条目 = 用默认"是两件事）
                if (keyText.Length == 0) continue;

                if (!ShortcutKeyText.TryParse(keyText, out var key))
                {
                    invalid.Add(id);
                    notes.Add($"认不识的键「{keyText}」（{id}）");
                    continue;
                }

                if (!keys.Contains(key) && keys.Count < 2) keys.Add(key);
            }

            foreach (var pair in requested)
            {
                // 只写了坏键：保留默认键，别把一条好好的命令变成"没有键"
                if (invalid.Contains(pair.Key) && pair.Value.Count == 0) continue;

                overrides[pair.Key] = pair.Value.ToArray();
            }

            warnings = notes;
            return overrides;
        }

        /// <summary>
        /// 把"生效的按键表"压成设置条目：只写与出厂默认不同的（含"清空成没有键"）。
        /// 顺序按命令表，读起来稳定。
        /// </summary>
        public static List<string> BuildEntries(IReadOnlyDictionary<PlayerCommandId, Keys[]> effective)
        {
            var entries = new List<string>();

            foreach (var definition in PlayerCommandCatalog.Defaults)
            {
                var current = effective.TryGetValue(definition.Id, out var keys)
                    ? keys
                    : definition.DefaultShortcuts.ToArray();

                if (current.SequenceEqual(definition.DefaultShortcuts)) continue;

                if (current.Length == 0)
                {
                    entries.Add(definition.Id + "=");
                    continue;
                }

                foreach (var key in current)
                    entries.Add(definition.Id + "=" + ShortcutKeyText.Format(key));
            }

            return entries;
        }

        /// <summary>
        /// 校验一份"生效的按键表"能不能保存。返回空表 = 可以。
        /// 两类问题：保留了别处的固定键；同一个键给了两条命令。
        /// </summary>
        public static List<string> Validate(IReadOnlyDictionary<PlayerCommandId, Keys[]> effective)
        {
            var errors = new List<string>();
            var owners = new Dictionary<Keys, PlayerCommandId>();

            foreach (var definition in PlayerCommandCatalog.Defaults)
            {
                var keys = effective.TryGetValue(definition.Id, out var custom)
                    ? custom
                    : definition.DefaultShortcuts.ToArray();

                foreach (var key in keys)
                {
                    foreach (var reserved in Reserved)
                    {
                        if (reserved.Key != key) continue;

                        errors.Add($"「{ShortcutKeyText.Format(key)}」已经被「{reserved.Reason}」占用，"
                                   + $"不能给「{definition.HelpText}」");
                    }

                    if (owners.TryGetValue(key, out var other))
                    {
                        var otherName = PlayerCommandCatalog.Defaults
                            .FirstOrDefault(item => item.Id == other)?.HelpText ?? other.ToString();

                        errors.Add($"「{ShortcutKeyText.Format(key)}」同时给了「{otherName}」和「{definition.HelpText}」");
                    }
                    else
                    {
                        owners[key] = definition.Id;
                    }
                }
            }

            return errors;
        }

        /// <summary>当前生效的按键表（每条命令一份，含没有键的）。</summary>
        public static Dictionary<PlayerCommandId, Keys[]> EffectiveMap()
        {
            var map = new Dictionary<PlayerCommandId, Keys[]>();

            foreach (var definition in PlayerCommandCatalog.All)
                map[definition.Id] = definition.AllShortcuts.ToArray();

            return map;
        }
    }
}
