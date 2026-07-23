using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace QuickTranslate.UI
{
    /// <summary>
    /// 系统托盘图标管理器
    /// </summary>
    public class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _contextMenu;
        private readonly System.Windows.Forms.Timer _singleClickTimer;
        private readonly ToolStripMenuItem _pauseResumeItem;
        private readonly ToolStripMenuItem _hotKeyToggleItem;
        private bool _isPaused;
        private bool _isHotKeyEnabled = true;

        /// <summary>
        /// 用户点击"设置"
        /// </summary>
        public event Action? SettingsRequested;

        public event Action? RestoreRequested;

        /// <summary>
        /// 用户点击"翻译历史"
        /// </summary>
        public event Action? HistoryRequested;

        public event Action? LogsRequested;

        /// <summary>
        /// 用户点击"暂停/恢复翻译"
        /// </summary>
        public event Action<bool>? PauseToggled;

        /// <summary>
        /// 用户点击"启用/禁用快捷键"
        /// </summary>
        public event Action<bool>? HotKeyToggled;

        /// <summary>
        /// 用户点击"退出"
        /// </summary>
        public event Action? ExitRequested;

        public TrayIconManager()
        {
            _singleClickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
            _singleClickTimer.Tick += (_, _) =>
            {
                _singleClickTimer.Stop();
                RestoreRequested?.Invoke();
            };

            // 创建右键菜单
            _contextMenu = new ContextMenuStrip();

            _pauseResumeItem = new ToolStripMenuItem("暂停翻译");
            _pauseResumeItem.Click += (s, e) =>
            {
                _isPaused = !_isPaused;
                _pauseResumeItem.Text = _isPaused ? "恢复翻译" : "暂停翻译";
                PauseToggled?.Invoke(_isPaused);
            };

            _hotKeyToggleItem = new ToolStripMenuItem("启用快捷键");
            _hotKeyToggleItem.CheckOnClick = true;
            _hotKeyToggleItem.Checked = true;
            _hotKeyToggleItem.Click += (s, e) =>
            {
                _isHotKeyEnabled = _hotKeyToggleItem.Checked;
                _hotKeyToggleItem.Text = _isHotKeyEnabled ? "启用快捷键" : "禁用快捷键";
                HotKeyToggled?.Invoke(_isHotKeyEnabled);
            };

            var settingsItem = new ToolStripMenuItem("设置");
            settingsItem.Click += (s, e) => SettingsRequested?.Invoke();

            var historyItem = new ToolStripMenuItem("翻译历史");
            historyItem.Click += (s, e) => HistoryRequested?.Invoke();

            var logsItem = new ToolStripMenuItem("日志查看");
            logsItem.Click += (s, e) => LogsRequested?.Invoke();

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => ExitRequested?.Invoke();

            _contextMenu.Items.Add(settingsItem);
            _contextMenu.Items.Add(historyItem);
            _contextMenu.Items.Add(logsItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_hotKeyToggleItem);
            _contextMenu.Items.Add(_pauseResumeItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(exitItem);

            // 创建托盘图标
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateDefaultIcon(),
                Text = "QuickTranslate - 划词翻译",
                ContextMenuStrip = _contextMenu,
                Visible = true
            };

            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _singleClickTimer.Stop();
                    _singleClickTimer.Start();
                }
            };

            // 双击托盘图标打开设置
            _notifyIcon.DoubleClick += (s, e) =>
            {
                _singleClickTimer.Stop();
                SettingsRequested?.Invoke();
            };
        }

        /// <summary>
        /// 创建默认托盘图标（紫色圆形 + "Q" 字母）
        /// </summary>
        private static Icon CreateDefaultIcon()
        {
            try
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("/QuickTranslate;component/Assets/QuickTranslate.ico", UriKind.Relative));
                if (resource is not null)
                {
                    using var source = new Icon(resource.Stream);
                    return new Icon(source, source.Size);
                }
            }
            catch
            {
                // Fall back to the generated icon when the packaged resource is unavailable.
            }

            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // 紫色圆形背景
                using var brush = new SolidBrush(Color.FromArgb(124, 58, 237)); // #7C3AED
                g.FillEllipse(brush, 1, 1, 30, 30);

                // 白色 "Q" 字母
                using var font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold);
                using var textBrush = new SolidBrush(Color.White);
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("Q", font, textBrush, new RectangleF(0, 0, 32, 32), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>
        /// 更新托盘提示文本
        /// </summary>
        public void UpdateToolTip(string text)
        {
            _notifyIcon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        /// <summary>
        /// 显示气泡提示
        /// </summary>
        public void ShowBalloonTip(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, int duration = 3000)
        {
            _notifyIcon.ShowBalloonTip(duration, title, message, icon);
        }

        /// <summary>
        /// 设置快捷键开关状态（外部同步）
        /// </summary>
        public void SetHotKeyEnabled(bool enabled)
        {
            _isHotKeyEnabled = enabled;
            _hotKeyToggleItem.Checked = enabled;
            _hotKeyToggleItem.Text = enabled ? "启用快捷键" : "禁用快捷键";
        }

        public void Dispose()
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
        }
    }
}
