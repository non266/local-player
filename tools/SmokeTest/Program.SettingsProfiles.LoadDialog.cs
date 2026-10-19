// 设置方案 —— 勾选窗口（一次列出全部分组、默认全勾、写得清"与当前不同"、控件不越界）。
//
// 从 Program.SettingsProfiles.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>勾选窗口：一次列出全部分组、默认全勾、写得清"与当前不同"、控件不越界。</summary>
        private static bool CheckSettingsLoadDialog()
        {
            var current = new AppSettings { Volume = 10, Theme = ThemeId.Light };
            var incoming = new AppSettings { Volume = 90, Theme = ThemeId.Dark, Deinterlace = "bob" };

            using var dialog = new SettingsLoadDialog(current, incoming, "外观方案");

            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(40, 40);
            dialog.Show();
            PumpMessages(200);

            var list = dialog.Controls.OfType<ListView>().FirstOrDefault();

            if (list == null || !list.CheckBoxes || list.Items.Count != SettingsProfile.All.Count)
            {
                Log(20, "载入对话框：分组清单不对（"
                        + (list == null ? "找不到清单" : $"{list.Items.Count} 行、勾选框 {list.CheckBoxes}")
                        + "）");
                return false;
            }

            if (dialog.SelectedSections.Count != SettingsProfile.All.Count || !dialog.CanLoad)
            {
                Log(20, $"载入对话框：默认没有全勾上（勾了 {dialog.SelectedSections.Count} 组）");
                return false;
            }

            // 「与当前不同」那一列：播放组只差音量一项（主题不在这一组里）
            var playbackRow = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => i.Text == SettingsProfile.Name(SettingsSection.Playback));

            if (playbackRow == null || playbackRow.SubItems[2].Text != "1 项")
            {
                Log(20, $"载入对话框：「播放」那一组的差异数不对（「{playbackRow?.SubItems[2].Text}」）");
                return false;
            }

            var sessionRow = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(i => i.Text == SettingsProfile.Name(SettingsSection.Session));

            if (sessionRow == null || sessionRow.SubItems[2].Text != "没有区别")
            {
                Log(20, $"载入对话框：「会话」两边一样却报了差异（「{sessionRow?.SubItems[2].Text}」）");
                return false;
            }

            // 控件不许越界（高 DPI 下最容易出问题）
            var scale = dialog.DeviceDpi / 96f;

            foreach (Control control in dialog.Controls)
            {
                if (control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height)
                    continue;

                Log(20, $"载入对话框在 {scale:0.##} 倍缩放下有控件越界："
                        + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                return false;
            }

            // 取消一组：结果里就没有它了
            dialog.SetChecked(SettingsSection.Playback, false);

            if (dialog.SelectedSections.Count != SettingsProfile.All.Count - 1 ||
                dialog.SelectedSections.Contains(SettingsSection.Playback))
            {
                Log(20, "载入对话框：取消一组之后交回去的结果不对");
                return false;
            }

            // 一组都不勾：载入按钮点不动，硬点也不生效
            dialog.SetAllChecked(false);

            if (dialog.CanLoad || dialog.SelectedSections.Count != 0)
            {
                Log(20, "载入对话框：一组都没勾时「载入」还是亮的");
                return false;
            }

            dialog.LoadSelected();

            if (dialog.DialogResult == DialogResult.OK)
            {
                Log(20, "载入对话框：一组都没勾，点「载入」居然生效了");
                return false;
            }

            // 全勾上再点：正常关掉并交出全部分组
            // （注意：非模态 Show 出来的窗口，Close() 会把它释放掉，
            //  所以"交回了什么"要在点之前看，点之后只能看 DialogResult）
            dialog.SetAllChecked(true);

            if (!dialog.CanLoad || dialog.SelectedSections.Count != SettingsProfile.All.Count)
            {
                Log(20, $"载入对话框：全勾上之后状态不对（勾了 {dialog.SelectedSections.Count} 组、"
                        + $"「载入」{(dialog.CanLoad ? "能点" : "是灰的")}）");
                return false;
            }

            dialog.LoadSelected();

            if (dialog.DialogResult != DialogResult.OK)
            {
                Log(20, "载入对话框：全勾上之后点「载入」没有生效");
                return false;
            }

            Log(20, $"载入对话框正常：一次列出全部 {SettingsProfile.All.Count} 组、默认全勾，"
                    + "每行写明「与当前不同」的项数（没区别的写「没有区别」），"
                    + "一组都不勾时「载入」点不动且硬点无效，DPI 缩放下控件没有越界");
            return true;
        }
    }
}
