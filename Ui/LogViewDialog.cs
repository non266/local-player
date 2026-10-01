using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 「查看日志」对话框：把最近若干条诊断记录摊开给人看，并给两个出口——
    /// 复制全文（贴进反馈里）和打开日志文件（自己翻更早的）。
    /// <para>
    /// 为什么要单独做一个小窗口而不是弹 MessageBox：出问题时用户要的是"能不能把这段发给你"，
    /// 而 MessageBox 里的文字选不全、也复制不走。
    /// </para>
    /// </summary>
    internal sealed class LogViewDialog : Form
    {
        private const int DesignWidth = 760;

        private readonly TextBox _text = new TextBox();
        private readonly Label _hint = new Label();
        private readonly Button _copyButton = new Button();
        private readonly Button _openButton = new Button();
        private readonly Button _closeButton = new Button();

        private LogViewDialog()
        {
            Text = "诊断日志";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = UiFonts.Sidebar;
            MinimumSize = new Size(480, 320);
            ClientSize = new Size(DesignWidth, 460);

            BuildLayout();
            Reload();
        }

        /// <summary>打开对话框。内容抄自 <see cref="AppLog"/> 的内存缓冲（不读文件）。</summary>
        public static void Show(IWin32Window owner, ThemePalette palette)
        {
            using var dialog = new LogViewDialog();

            dialog.ApplyPalette(palette);
            dialog.ShowDialog(owner);
        }

        private void BuildLayout()
        {
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.BorderStyle = BorderStyle.FixedSingle;
            _text.Font = UiFonts.Resolve("Consolas", 9f, FontStyle.Regular);

            _hint.AutoSize = false;

            _copyButton.Text = "复制全部";
            _copyButton.Click += (s, e) => CopyAll();

            _openButton.Text = "打开日志文件";
            _openButton.Click += (s, e) => OpenLogFile();

            _closeButton.Text = "关闭";
            _closeButton.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _hint, _text, _copyButton, _openButton, _closeButton });

            CancelButton = _closeButton;

            Resize += (s, e) => ApplyLayout();
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            var padding = 12;
            var gap = 8;
            var buttonHeight = 30;
            var buttonWidth = 110;

            _hint.Bounds = new Rectangle(padding, padding, Math.Max(80, ClientSize.Width - padding * 2), 22);

            var buttonTop = ClientSize.Height - padding - buttonHeight;

            _closeButton.Bounds = new Rectangle(
                ClientSize.Width - padding - buttonWidth, buttonTop, buttonWidth, buttonHeight);

            _openButton.Bounds = new Rectangle(
                _closeButton.Left - gap - buttonWidth, buttonTop, buttonWidth, buttonHeight);

            _copyButton.Bounds = new Rectangle(
                _openButton.Left - gap - buttonWidth, buttonTop, buttonWidth, buttonHeight);

            _text.Bounds = new Rectangle(
                padding,
                _hint.Bottom + gap,
                Math.Max(80, ClientSize.Width - padding * 2),
                Math.Max(60, buttonTop - gap - (_hint.Bottom + gap)));
        }

        private void Reload()
        {
            var path = AppLog.FilePath;

            _hint.Text = path == null
                ? "本次没有写日志文件；下面是内存里最近 " + AppLog.BufferLimit + " 条记录。"
                : "日志文件：" + path + "（下面是最近 " + AppLog.BufferLimit + " 条）";

            var lines = AppLog.Snapshot(AppLog.BufferLimit);

            _text.Text = lines.Count == 0
                ? "（还没有任何记录）"
                : string.Join(Environment.NewLine, lines);

            // 光标停到最后一行：看日志的人关心的基本都是"刚刚那一下"
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }

        private void CopyAll()
        {
            try
            {
                Clipboard.SetText(_text.Text);
                _hint.Text = "已复制 " + AppLog.Snapshot(0).Count + " 条记录到剪贴板。";
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("复制日志到剪贴板失败（剪贴板被别的程序占着时会这样）", ex);
                MessageBox.Show(this, ex.Message, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenLogFile()
        {
            var path = AppLog.FilePath ?? AppLog.DefaultFilePath;

            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    // 没写文件（或者还没写过）：退而求其次，把数据目录打开
                    var folder = Path.GetDirectoryName(path) ?? AppLog.DefaultFilePath;

                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("打开日志文件失败", ex);
                MessageBox.Show(this, ex.Message, "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.WindowBack;
            ForeColor = palette.ListFore;

            _text.BackColor = palette.ListBack;
            _text.ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }
        }
    }
}
