using System;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 把菜单项 / 工具条按钮 / 普通按钮绑到一条命令上，并按统一的状态快照刷新它们。
    /// <para>
    /// 绑定只做三件事：<b>把快捷键显示串从目录抄过去</b>（不再让设计器里那份手写文本漂走）、
    /// 点下去调同一个执行入口、勾选类命令关掉 <c>CheckOnClick</c>（否则用户点一下
    /// WinForms 先替我们翻一次、命令又翻一次，等于没点）。菜单的文字一个字都不改——
    /// 冒烟测试与用户都按文字找菜单项。
    /// </para>
    /// </summary>
    internal static class CommandBinder
    {
        /// <summary>绑定一个菜单项 / 工具条项。重复绑定同一条命令到多项（工具栏的 ▶ 与 ⏸）是允许的。</summary>
        public static void Bind(ToolStripItem item, PlayerCommandId id, Action<PlayerCommandId> execute)
        {
            var definition = PlayerCommandCatalog.Get(id);

            if (item is ToolStripMenuItem menuItem)
            {
                // 快捷键显示串只在目录里有键时才覆盖：没有键的命令不该把菜单上原来那句抹掉。
                if (definition.MenuDisplay != null)
                    menuItem.ShortcutKeyDisplayString = definition.MenuDisplay;

                if (definition.Checkable)
                {
                    // 勾选态由命令自己翻、再由 RefreshCommandSurfaces 推回来：
                    // 放着 CheckOnClick 会变成"点一下 WinForms 先翻一次，命令又翻一次"。
                    menuItem.CheckOnClick = false;
                }
            }

            item.Click += (s, e) => execute(id);
        }

        /// <summary>绑定一个普通 <see cref="Button"/>（音频 / 迷你卡片上的播放控件条）。</summary>
        public static void Bind(Button button, PlayerCommandId id, Action<PlayerCommandId> execute)
        {
            button.Click += (s, e) => execute(id);
        }

        /// <summary>按当前状态刷新一个已绑定的菜单项 / 工具条项（可用性 + 勾选态）。</summary>
        public static void Apply(
            ToolStripItem item,
            PlayerCommandId id,
            CommandAvailability availability,
            Func<PlayerCommandId, bool> isChecked)
        {
            var canExecute = PlayerCommandCatalog.CanExecute(id, availability);
            if (item.Enabled != canExecute) item.Enabled = canExecute;

            if (item is not ToolStripMenuItem menuItem) return;
            if (!PlayerCommandCatalog.Get(id).Checkable) return;

            var shouldCheck = isChecked(id);
            if (menuItem.Checked != shouldCheck) menuItem.Checked = shouldCheck;
        }
    }
}
