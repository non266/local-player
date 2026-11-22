using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 快捷键的文本形态（1.29.0）：<see cref="Keys"/> ↔ 菜单 / 设置文件里的那一串字。
    /// <para>
    /// 以前只有"画出来"这一半（<c>PlayerCommandCatalog.FormatKeys</c>），因为键都是写死的；
    /// 1.29.0 起用户能改键、改完要落盘，就必须有<b>认回来</b>的那一半。两半放在一起，
    /// 免得"写得出来、读不回来"（这种错只会在重启之后才暴露）。
    /// </para>
    /// <para>
    /// 字面量与 1.11.0 起菜单上的一致：<c>Ctrl+Alt+Shift</c> + <c>Space</c> / <c>←→↑↓</c> /
    /// <c>PgUp</c> / <c>PgDn</c> / <c>Enter</c> / <c>Esc</c> / <c>Delete</c> / <c>[</c> / <c>]</c> /
    /// 字母数字 / <c>F1</c>～<c>F24</c>。
    /// </para>
    /// </summary>
    internal static class ShortcutKeyText
    {
        /// <summary>把一个 <see cref="Keys"/> 画成菜单 / 帮助 / 设置文件里的样子。</summary>
        public static string Format(Keys keys)
        {
            var parts = new List<string>();

            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt) != 0) parts.Add("Alt");
            if ((keys & Keys.Shift) != 0) parts.Add("Shift");

            var code = keys & Keys.KeyCode;

            parts.Add(code switch
            {
                Keys.Space => "Space",
                Keys.Left => "←",
                Keys.Right => "→",
                Keys.Up => "↑",
                Keys.Down => "↓",
                Keys.PageUp => "PgUp",
                Keys.PageDown => "PgDn",
                Keys.Enter => "Enter",
                Keys.Escape => "Esc",
                Keys.Delete => "Delete",
                Keys.Back => "Backspace",
                Keys.Tab => "Tab",
                Keys.OemCloseBrackets => "]",
                Keys.OemOpenBrackets => "[",
                Keys.D0 => "0",
                Keys.D1 => "1",
                Keys.D2 => "2",
                Keys.D3 => "3",
                Keys.D4 => "4",
                Keys.D5 => "5",
                Keys.D6 => "6",
                Keys.D7 => "7",
                Keys.D8 => "8",
                Keys.D9 => "9",
                _ => code.ToString()
            });

            return string.Join("+", parts);
        }

        /// <summary>
        /// 把一串字认回 <see cref="Keys"/>（<see cref="Format"/> 的逆）。认不出返回 <c>false</c>：
        /// 调用方要记一条说明，不能悄悄当成默认键。
        /// </summary>
        public static bool TryParse(string? text, out Keys keys)
        {
            keys = Keys.None;

            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0) return false;

            var parts = trimmed.Split('+');
            if (parts.Length == 0) return false;

            var result = Keys.None;

            for (var i = 0; i < parts.Length - 1; i++)
            {
                var modifier = parts[i].Trim();

                if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    result |= Keys.Control;
                else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    result |= Keys.Alt;
                else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    result |= Keys.Shift;
                else
                    return false;
            }

            var name = parts[^1].Trim();
            if (name.Length == 0) return false;

            var code = ParseCode(name);
            if (code == Keys.None) return false;

            // "Ctrl+Shift" 这种只有修饰键的写法不是快捷键：它按下去什么都不会发生
            if ((code & Keys.Modifiers) != 0) return false;

            keys = result | code;
            return true;
        }

        /// <summary>只认按键本身（不带修饰）。认不出返回 <see cref="Keys.None"/>。</summary>
        private static Keys ParseCode(string name)
        {
            switch (name)
            {
                case "Space": return Keys.Space;
                case "←": case "Left": return Keys.Left;
                case "→": case "Right": return Keys.Right;
                case "↑": case "Up": return Keys.Up;
                case "↓": case "Down": return Keys.Down;
                case "PgUp": case "PageUp": return Keys.PageUp;
                case "PgDn": case "PageDown": return Keys.PageDown;
                case "Enter": case "Return": return Keys.Enter;
                case "Esc": case "Escape": return Keys.Escape;
                case "Delete": case "Del": return Keys.Delete;
                case "Backspace": case "Back": return Keys.Back;
                case "Tab": return Keys.Tab;
                case "[": return Keys.OemOpenBrackets;
                case "]": return Keys.OemCloseBrackets;
            }

            if (name.Length == 1)
            {
                var ch = char.ToUpperInvariant(name[0]);

                if (ch >= 'A' && ch <= 'Z') return (Keys)ch;              // Keys.A == 'A'
                if (ch >= '0' && ch <= '9') return Keys.D0 + (ch - '0');  // Keys.D0..D9 是连续的
            }

            // F1～F24 与枚举里认得的名字（Home / End / Insert / 小键盘那些）
            if (Enum.TryParse<Keys>(name, ignoreCase: true, out var parsed) &&
                (parsed & Keys.Modifiers) == Keys.None &&
                parsed != Keys.None)
            {
                return parsed;
            }

            return Keys.None;
        }

        /// <summary>测试用：这个键是不是"纯修饰键"（Ctrl / Alt / Shift 本身不能当快捷键）。</summary>
        internal static bool IsBareModifier(Keys keys) =>
            (keys & Keys.KeyCode) == Keys.None || (keys & Keys.KeyCode) is Keys.ControlKey or Keys.Menu or Keys.ShiftKey;
    }
}
