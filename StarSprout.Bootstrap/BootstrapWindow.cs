using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace StarSprout.Bootstrap
{
    internal sealed class BootstrapWindow : Form
    {
        private readonly BootstrapCore core;
        private readonly string[] arguments;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly Label status;
        private readonly ProgressBar progress;
        private readonly Button cancelButton;
        private bool busy;

        public BootstrapWindow(BootstrapCore core, string[] arguments)
        {
            this.core = core;
            this.arguments = arguments;
            Text = "星芽启动器 · 准备运行环境";
            ClientSize = new Size(480, 230);
            MinimumSize = new Size(496, 269);
            MaximumSize = MinimumSize;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 252, 249);
            Font = new Font("Microsoft YaHei UI", 10);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            var title = new Label { Text = "星芽正在准备好一切", AutoSize = true, Location = new Point(26, 22), Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = Color.FromArgb(34, 105, 70) };
            status = new Label { Text = "正在检查 .NET 10 与 WebView2…", Location = new Point(28, 64), Size = new Size(425, 52), ForeColor = Color.FromArgb(61, 77, 67) };
            progress = new ProgressBar { Location = new Point(28, 122), Size = new Size(425, 10), Style = ProgressBarStyle.Marquee };
            var detail = new Label { Text = "缺少的运行库会从微软官方下载安装。\n系统安装提示需要你允许；离线包优先使用内置安装程序。", Location = new Point(28, 146), Size = new Size(330, 55), Font = new Font(Font.FontFamily, 9), ForeColor = Color.FromArgb(106, 119, 110) };
            cancelButton = new Button { Text = "取消", Location = new Point(370, 168), Size = new Size(84, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            cancelButton.Click += (sender, e) => Cancel();
            Controls.AddRange(new Control[] { title, status, progress, detail, cancelButton });
            Shown += Begin;
            FormClosing += (sender, e) => { if (busy) { e.Cancel = true; Cancel(); } };
        }

        private void Cancel()
        {
            cancel.Cancel();
            cancelButton.Enabled = false;
            status.Text = "正在取消。若安装已开始，将在当前安装结束后退出…";
        }

        private async void Begin(object sender, EventArgs e)
        {
            busy = true;
            try
            {
                if (!Environment.Is64BitOperatingSystem) throw new PlatformNotSupportedException("当前版本需要 64 位 Windows 10 / 11。");
                if (!File.Exists(core.AppPath)) throw new FileNotFoundException("缺少 app\\StarSprout.App.exe。请先完整解压启动器。", core.AppPath);
                await core.EnsureAsync((message, percent) => {
                    if (IsDisposed || cancel.IsCancellationRequested) return;
                    if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(message, percent))); return; }
                    SetProgress(message, percent);
                }, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                core.Launch(arguments);
            }
            catch (OperationCanceledException) { Environment.ExitCode = 3; }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                MessageBox.Show(this, ex.Message + "\n\n可以下载包含完整运行库的离线版，解压后重新打开。", "运行环境准备失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                Close();
            }
        }

        private void SetProgress(string message, int percent)
        {
            if (cancel.IsCancellationRequested) return;
            status.Text = message;
            progress.Style = percent < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (percent >= 0) progress.Value = Math.Max(0, Math.Min(100, percent));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cancel.Dispose();
            base.Dispose(disposing);
        }
    }
}
