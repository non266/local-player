using System;
using System.Drawing;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 极简的文本输入对话框（WinForms 在 .NET 下没有内置的 InputBox）。
    /// <para>
    /// 布局<b>按测量结果摆</b>，不做"提示有几行 × 行高"这种估算：
    /// 提示文字行数一多（服务地址那个提示就有四行），估算少一行，
    /// 文本框就会压在最后一行上——这个真实踩过。
    /// </para>
    /// </summary>
    internal sealed class InputDialog : Form
    {
        private const int DesignWidth = 480;

        private readonly Label _label;
        private readonly TextBox _textBox;
        private readonly Button _okButton;
        private readonly Button _cancelButton;

        internal InputDialog(string title, string prompt, string defaultValue, bool mask)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            _label = new Label
            {
                Text = prompt,
                AutoSize = true,
                MaximumSize = new Size(Scaled(DesignWidth - 28), 0)
            };

            _textBox = new TextBox { Text = defaultValue, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

            // 密钥这种不该明晃晃显示在屏幕上的东西，给一个掩码的版本
            if (mask) _textBox.UseSystemPasswordChar = true;

            _okButton = new Button { Text = "确定", DialogResult = DialogResult.OK };
            _cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel };

            Controls.AddRange(new Control[] { _label, _textBox, _okButton, _cancelButton });

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            ApplyLayout();
            _textBox.SelectAll();
            ActiveControl = _textBox;
        }

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄就绪之后 DeviceDpi 才是真实值，这时再按真实 DPI 摆一次
            ApplyLayout();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyLayout();
        }

        /// <summary>
        /// 摆一遍：提示 → 输入框 → 按钮，每一段都接在上一段的真实高度下面。
        /// <para>
        /// 不能用固定坐标：提示可能有 1 行也可能有 4 行，字号还会随 DPI 变，
        /// 而 <see cref="Label.PreferredSize"/> 给的正是"这段文字在这个字体下要多高"。
        /// </para>
        /// </summary>
        private void ApplyLayout()
        {
            var padding = Scaled(14);
            var gap = Scaled(12);
            var buttonWidth = Scaled(82);
            var buttonHeight = Scaled(30);

            var width = Math.Max(Scaled(320), Scaled(DesignWidth));

            _label.Location = new Point(padding, padding);
            _label.MaximumSize = new Size(width - padding * 2, 0);

            var labelHeight = _label.PreferredSize.Height;

            _textBox.Bounds = new Rectangle(
                padding,
                padding + labelHeight + gap,
                width - padding * 2,
                Math.Max(Scaled(24), _textBox.PreferredHeight));

            var buttonTop = _textBox.Bottom + gap;

            _cancelButton.Bounds = new Rectangle(width - padding - buttonWidth, buttonTop, buttonWidth, buttonHeight);
            _okButton.Bounds = new Rectangle(_cancelButton.Left - Scaled(8) - buttonWidth, buttonTop, buttonWidth, buttonHeight);

            ClientSize = new Size(width, buttonTop + buttonHeight + padding);
        }

        /// <summary>弹出输入框；用户取消时返回 <c>null</c>。</summary>
        /// <param name="mask">是否把输入内容掩成圆点（填密钥用）。</param>
        public static string? Show(
            IWin32Window owner, string title, string prompt, string defaultValue = "", bool mask = false)
        {
            using (var dialog = new InputDialog(title, prompt, defaultValue, mask))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._textBox.Text.Trim() : null;
            }
        }
    }
}
