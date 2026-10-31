using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WtClickerGUI
{
    /// <summary>
    /// 主窗体（2026-10-30 UI 第二版）：
    /// - 可拉伸窗口（MinimumSize 限制，所有区域 Dock/Anchor 随窗口缩放）；
    /// - 奶油暖色主题匹配 WuwaTappo 桌宠风格；卡片式布局，行距宽松不再拥挤；
    /// - 状态卡：当前次数 / 距下一宝箱 / 刷新倒计时 / 本次连点；
    /// - 日志深色控制台风，按级别着色，并同步落盘 WtClicker.log 便于排查。
    /// </summary>
    internal class MainForm : Form
    {
        readonly ClickerEngine _engine = new ClickerEngine();
        readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();

        HeaderBar _header;
        StatCard _cardCount, _cardNeed, _cardCd, _cardRate, _cardStat;
        NumericUpDown _numCps;
        Label _tipCps;
        ComboBox _cmbChannel;
        CheckBox _chkChest, _chkHuman, _chkPaced;
        RoundedButton _btnStart, _btnTest;
        Label _lblLive;
        RichTextBox _rtbLog;

        // ---- 主题色
        static readonly Color Ink = Color.FromArgb(74, 52, 35);        // 深棕（主文字）
        static readonly Color InkSoft = Color.FromArgb(156, 139, 117); // 浅棕（次要文字）
        static readonly Color Accent = Color.FromArgb(185, 106, 60);   // 焦糖橙（主按钮）
        static readonly Color AccentDark = Color.FromArgb(158, 87, 45);
        static readonly Color Border_ = Color.FromArgb(232, 220, 198); // 卡片描边
        static readonly Color PageBg = Color.FromArgb(246, 241, 231);  // 页面底色

        public MainForm()
        {
            // DPI 缩放策略：.NET 8 下 AutoScaleMode.Dpi 在构造期 DeviceDpi 仍为 96，
            // Load 后不再触发，导致 150% 屏窗口不放大（实测 windowDPI=144 而物理 860）。
            // 改为 OnLoad 里按真实 DeviceDpi 手动 Scale —— 见 OnLoad override。
            // 标题带构建时间（取 exe 自身修改时间，发布即变，杜绝「跑的是旧版」的困惑）
            DateTime built = System.IO.File.GetLastWriteTime(Application.ExecutablePath);
            Text = $"WuwaTappo 连点器  [构建 {built:MM-dd HH:mm}]";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PageBg;
            Size = new Size(860, 820);
            MinimumSize = new Size(860, 820);
            Padding = new Padding(14, 10, 14, 12);
            Font = new Font("Microsoft YaHei UI", 9F);

            // ================= 头部（Dock=Top，最后添加保证最顶） =================
            _header = new HeaderBar { Dock = DockStyle.Top, Height = 96 };

            // ================= 状态卡行（TableLayoutPanel 五等分，随窗口拉伸） =================
            var statTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 90, ColumnCount = 5, RowCount = 1,
                Margin = new Padding(0), Padding = new Padding(0)
            };
            for (int i = 0; i < 5; i++)
                statTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            statTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _cardCount = NewCard("当前次数（视觉读取）");
            _cardNeed = NewCard("距下一宝箱");
            _cardCd = NewCard("宝箱刷新倒计时");
            _cardRate = NewCard("实时点击速率");
            _cardStat = NewCard("本次连点 · 宝箱");
            statTable.Controls.Add(_cardCount, 0, 0);
            statTable.Controls.Add(_cardNeed, 1, 0);
            statTable.Controls.Add(_cardCd, 2, 0);
            statTable.Controls.Add(_cardRate, 3, 0);
            statTable.Controls.Add(_cardStat, 4, 0);

            // ================= 设置卡（Dock=Top，行距宽松） =================
            var cfg = new RoundedPanel
            {
                Dock = DockStyle.Top, Height = 214,
                BackColor = Color.White, BorderColor = Border_, Padding = new Padding(18, 14, 18, 10)
            };
            cfg.Controls.Add(MkLabel("最快速率上限", 18, 22, cfg, Ink, 9.5F, bold: true));
            _numCps = new NumericUpDown
            {
                Location = new Point(120, 17),
                Size = new Size(110, 28),
                Minimum = 1, Maximum = 30, Value = 10,
                DecimalPlaces = 1, Increment = 0.5M,
                BorderStyle = BorderStyle.FixedSingle
            };
            _tipCps = MkLabel("10.0 CPS（上限）", 244, 22, cfg, Accent, 10.5F, bold: true);
            _numCps.ValueChanged += (s, e) => { _tipCps.Text = CpsText(); ApplyConfig(); };
            cfg.Controls.Add(_numCps);
            // 注意：不要用 Anchor.Right —— OnLoad 的 Scale() 会把 anchor 右对齐的
            // AutoSize label 推出窗口外。固定 x 即可。

            cfg.Controls.Add(MkLabel(
                "实际速率由程序按画面自主实时调节：无气泡全速（=上限）；气泡排队期把缺口按剩余冷却摊成匀速（≤上限），攒满即暂停防溢出；宝箱弹出立即点开",
                18, 62, cfg, InkSoft, 8.5F));

            cfg.Controls.Add(MkLabel("注入通道", 18, 108, cfg, Ink, 9.5F, bold: true));
            _cmbChannel = new ComboBox
            {
                Location = new Point(120, 102), Size = new Size(320, 28),
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cmbChannel.Items.AddRange(new object[]
            {
                "自动（DesktopCatInput / 主窗，均已验证计数）",
                "DesktopCatInput",
                "主窗 Tauri Window"
            });
            _cmbChannel.SelectedIndex = 0;
            _cmbChannel.SelectedIndexChanged += (s, e) => ApplyConfig();
            cfg.Controls.Add(_cmbChannel);

            _chkChest = NewCheck("宝箱自动点击（弹出后立即点击 —— 连续 2 个后，后续宝箱变为 30 分钟倒计时气泡，气泡消失即自动点击）", 18, 150, cfg);
            _chkChest.CheckedChanged += (s, e) => ApplyConfig();
            _chkHuman = NewCheck("拟人化节奏（抖动 + 微/长休息，防「点击数异常」检测；宝箱紧迫时自动跳过长休息）", 18, 182, cfg);
            _chkHuman.CheckedChanged += (s, e) => ApplyConfig();
            _chkPaced = NewCheck("气泡期匀速（把门槛缺口按剩余冷却摊成低速匀速点击，无突发更平滑；宝箱产出速率不变）", 18, 214, cfg);
            _chkPaced.Checked = true;
            _chkPaced.CheckedChanged += (s, e) => ApplyConfig();

            // ================= 按钮行（Dock=Top） =================
            var btnPanel = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = PageBg };
            _btnStart = new RoundedButton
            {
                Text = "开始连点",
                Location = new Point(2, 8),
                Size = new Size(190, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Base = Accent, Hover = AccentDark, Press = Color.FromArgb(136, 73, 36)
            };
            _btnStart.Click += BtnStart_Click;
            btnPanel.Controls.Add(_btnStart);

            _btnTest = new RoundedButton
            {
                Text = "测试一次",
                Location = new Point(206, 8),
                Size = new Size(120, 48),
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Base = Color.FromArgb(240, 233, 219), Hover = Color.FromArgb(230, 220, 200),
                Press = Color.FromArgb(220, 208, 184),
                ForeColor = Ink
            };
            _btnTest.Click += BtnTest_Click;
            btnPanel.Controls.Add(_btnTest);

            _lblLive = new Label
            {
                Text = "待机中 — 启动 Wuwa Tappo 后点「测试一次」确认注入有效，再「开始连点」",
                Location = new Point(2, 64), Size = new Size(btnPanel.Width - 4, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = InkSoft, Font = new Font("Microsoft YaHei UI", 9F),
                AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
            };
            btnPanel.Controls.Add(_lblLive);

            // ================= 日志（Dock=Fill 填满剩余空间） =================
            var logCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(43, 36, 30), BorderColor = Border_,
                Padding = new Padding(12)
            };
            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(43, 36, 30),
                ForeColor = Color.FromArgb(232, 220, 198),
                Font = new Font("Consolas", 9.25F),
                ReadOnly = true,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            logCard.Controls.Add(_rtbLog);

            // Dock 加载顺序：Fill 最先加，Top 依次加（后加的在更外侧）
            Controls.Add(logCard);
            Controls.Add(btnPanel);
            Controls.Add(cfg);
            Controls.Add(statTable);
            Controls.Add(_header);

            // ---- 引擎日志回调（按内容分级着色，marshal 到 UI 线程；同步落盘便于排查）
            _engine.Log = s => BeginInvoke(() => AppendLog(s));

            // ---- UI 轮询：状态卡 300ms；空闲时每 tick 调 ReadIdleVision
            //      （引擎内部节流 0.5s 实扫一帧读「次数+倒计时」，待机也实时跟随），
            //      WuwaTappo 在线状态共用同一帧
            _uiTimer.Interval = 100;   // 0.1s 刷新：卡片与状态行跟手，数据源引擎 0.5s 持续重建
            _uiTimer.Tick += (s, e) =>
            {
                RefreshCards();
                int blobs = _engine.ReadIdleVision();   // 引擎运行时返回 -2，由引擎自己读
                if (blobs == -1)
                {
                    _header.SetStatus("WuwaTappo 未运行", false);
                    return;
                }
                if (blobs >= 0) _header.SetStatus("WuwaTappo 运行中", blobs > 0);
            };
            _uiTimer.Start();

            FormClosing += (s, e) => { _uiTimer.Stop(); SaveConfig(); _engine.Shutdown(); };

            LoadConfig(); ApplyConfig();
            AppendLog($"程序构建时间 {System.IO.File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm:ss}（若与最新发布不符说明运行的是旧 exe）。", 4);
            AppendLog("① 启动 Wuwa Tappo；② 点「测试一次」确认注入通道有效；③ 点「开始连点」。", 4);
            AppendLog("点击落在屏幕空白点——不触碰桌宠、不抢鼠标。计数是否生效自动验证。", 4);
            AppendLog("自动流程：识别桌宠下方「当前次数」→ 每攒 1000 立即点开宝箱；", 4);
            AppendLog("连续领 2 个后，后续宝箱变为 30 分钟倒计时气泡 → 气泡 = 门槛已攒满在排队，", 4);
            AppendLog("期间自动暂停点击防计数溢出，气泡消失宝箱弹出即自动点击。", 4);
            AppendLog("建议：挂机前关闭应用内「游戏模式」，开启「总是展示宝箱」。", 4);
        }

        // ---------------------------------------------------------------- DPI 缩放
        // 布局按 96dpi 设计（860x820）。窗口实际 DPI 更高时整体等比放大：
        // Scale() 缩放全部子控件位置/尺寸；字体为 pt 指定，GDI 会按窗口 DPI 自动
        // 渲染成对应物理大小，与放大后的布局框匹配 —— 文字不再拥挤截断。
        const float DesignDpi = 96f;
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float factor = DeviceDpi / DesignDpi;
            if (factor > 1.01f || factor < 0.99f)
            {
                MinimumSize = Size.Empty;
                Scale(new SizeF(factor, factor));
                Size = new Size((int)(860 * factor), (int)(820 * factor));
                MinimumSize = new Size((int)(860 * factor), (int)(820 * factor));
            }
        }

        // ---------------------------------------------------------------- UI 构建辅助
        StatCard NewCard(string caption)
        {
            return new StatCard
            {
                Caption = caption,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 10, 0)
            };
        }

        static Label MkLabel(string text, int x, int y, Control parent, Color color, float size, bool bold = false)
        {
            var l = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = true,
                ForeColor = color,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
            parent.Controls.Add(l);
            return l;
        }

        static CheckBox NewCheck(string text, int x, int y, Control parent)
        {
            var c = new CheckBox
            {
                Text = text, Location = new Point(x, y), AutoSize = true,
                ForeColor = Ink, Checked = true,
                Font = new Font("Microsoft YaHei UI", 8.75F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            parent.Controls.Add(c);
            return c;
        }

        string CpsText() => $"{_numCps.Value:0.#} CPS（上限）";

        // ---------------------------------------------------------------- 状态刷新
        void RefreshCards()
        {
            // 次数：读到过就一直显示（引擎 0.5s 持续刷新）；新鲜度只影响颜色，
            // 不再因 10s 窗口回退成「—」（日志证明引擎在读数时 UI 曾误判过期）
            long cv = _engine.CountVal;
            bool fresh = _engine.CountFresh;
            if (cv >= 0)
            {
                _cardCount.Value = cv.ToString("N0");
                _cardCount.ValueColor = fresh ? Ink : InkSoft;
            }
            else if (_engine.WantRun)
            {
                // 引擎已启动但 OCR 尚未首次读到（如启动瞬间/桌宠被遮挡）——如实说明而非「—」
                _cardCount.Value = "读取中…";
                _cardCount.ValueColor = InkSoft;
            }
            else
            {
                _cardCount.Value = "—";
                _cardCount.ValueColor = InkSoft;
            }

            int queued = _engine.QueuedBubbles;
            if (queued > 0)
            {
                _cardNeed.Value = $"{queued} 个排队 ✓";
                _cardNeed.ValueColor = Color.FromArgb(96, 150, 90);
            }
            else
            {
                int need = _engine.NeededClicks;
                _cardNeed.Value = need < 0 ? "—" : need == 0 ? "已攒够 ✓" : $"还需 {need:N0} 次";
                _cardNeed.ValueColor = need == 0 ? Color.FromArgb(96, 150, 90) : Ink;
            }

            double cd = _engine.CdRemain;
            _cardCd.Value = cd < 0 ? "无" : TimeSpan.FromSeconds(cd).ToString(@"mm\:ss");
            _cardCd.ValueColor = cd < 0 ? InkSoft : Color.FromArgb(196, 106, 72);

            _cardStat.Value = $"{_engine.Clicks:N0} · {_engine.Chests}";

            // 实时点击速率：4s 滑窗真实值（暂停/宝箱点击期间趋 0，全速期≈上限）
            double acps = _engine.ActualCps;
            _cardRate.Value = acps < 0.05 ? "0 /s" : $"{acps:F1} /s";
            _cardRate.ValueColor = acps < 0.05 ? InkSoft : Color.FromArgb(96, 150, 90);

            // 状态行：引擎运行时显示与心跳日志同格式的实时快照（引擎 ~0.4-0.5s 重建，
            // 此处 300ms 同步），待机/未启动时回退到桌宠检测状态文本
            string live = _engine.StatusLine;
            if (string.IsNullOrEmpty(live)) live = _engine.LiveDesc;
            if (!string.IsNullOrEmpty(live)) _lblLive.Text = live;
            _header.Running = _engine.WantRun;
            _header.Invalidate();
        }

        // ---------------------------------------------------------------- 日志
        void AppendLog(string s, int forcedLevel = -1)
        {
            // 所有日志（含 UI 侧如「▶ 已开始」）统一落盘，便于事后排查
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "WtClicker.log"),
                $"[{DateTime.Now:HH:mm:ss}] {s}\r\n"); } catch { }
            int level = forcedLevel >= 0 ? forcedLevel : Classify(s);
            Color c = level switch
            {
                0 => Color.FromArgb(232, 220, 198),   // 普通
                1 => Color.FromArgb(143, 209, 143),   // 成功
                2 => Color.FromArgb(240, 179, 87),    // 警告
                3 => Color.FromArgb(242, 126, 126),   // 错误
                _ => Color.FromArgb(167, 155, 138),   // 暗淡提示
            };
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionColor = Color.FromArgb(122, 110, 96);
            _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] ");
            _rtbLog.SelectionColor = c;
            _rtbLog.AppendText(s + "\n");
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();

            // 长时间挂机保护：日志框超过 600 行时裁掉最老的 300 行（文件里是全量的，仅省 UI 内存）
            if (_rtbLog.Lines.Length > 600)
            {
                var all = _rtbLog.Lines;
                _rtbLog.Clear();
                _rtbLog.Lines = all[^300..];
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();
            }
        }

        static int Classify(string s)
        {
            if (s.StartsWith("✅")) return 1;
            if (s.StartsWith("⚠️") || s.StartsWith("⏱")) return 2;
            if (s.Contains("失败") || s.Contains("异常")) return 3;
            if (s.StartsWith("▶") || s.StartsWith("⏸")) return 4;
            return 0;
        }

        // ---------------------------------------------------------------- 配置与按钮
        void ApplyConfig()
        {
            _engine.Cps = (double)_numCps.Value;   // 仅为上限；实际速率由引擎按倒计时自主调节
            _engine.AutoChest = _chkChest.Checked;
            _engine.Humanize = _chkHuman.Checked;
            _engine.PacedBubble = _chkPaced.Checked;
            _engine.Channel = _cmbChannel.SelectedIndex;
        }

        // ---------------------------------------------------------------- 配置持久化（借鉴 WuwaTappoClicker 的 config.json）
        string ConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.ini");

        void SaveConfig()
        {
            try
            {
                System.IO.File.WriteAllLines(ConfigPath, new[]
                {
                    "maxCps=" + ((double)_numCps.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "autoChest=" + _chkChest.Checked,
                    "humanize=" + _chkHuman.Checked,
                    "pacedBubble=" + _chkPaced.Checked,
                    "channel=" + _cmbChannel.SelectedIndex,
                });
            }
            catch { /* 落盘失败不影响运行 */ }
        }

        void LoadConfig()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigPath)) return;
                foreach (var line in System.IO.File.ReadAllLines(ConfigPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
                    switch (k)
                    {
                        case "maxCps" when double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double mc):
                            _numCps.Value = Math.Clamp((decimal)mc, _numCps.Minimum, _numCps.Maximum); break;
                        case "cpsX2" when int.TryParse(v, out int cps):   // 旧版滑条配置迁移：值/2 = CPS
                            _numCps.Value = Math.Clamp(cps / 2.0m, _numCps.Minimum, _numCps.Maximum); break;
                        case "autoChest" when bool.TryParse(v, out bool ac): _chkChest.Checked = ac; break;
                        case "humanize" when bool.TryParse(v, out bool hu): _chkHuman.Checked = hu; break;
                        case "pacedBubble" when bool.TryParse(v, out bool pb): _chkPaced.Checked = pb; break;
                        case "channel" when int.TryParse(v, out int ch):
                            _cmbChannel.SelectedIndex = Math.Clamp(ch, 0, _cmbChannel.Items.Count - 1); break;
                    }
                }
            }
            catch { /* 配置损坏则用默认值 */ }
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            ApplyConfig();
            if (!_engine.WantRun)
            {
                _engine.WantRun = true;
                if (!_engine.Started) { _engine.Start(); _engine.Started = true; }
                _btnStart.Text = "停止";
                _btnStart.Base = Color.FromArgb(169, 84, 74);
                _btnStart.Hover = Color.FromArgb(150, 70, 60);
                _btnStart.Press = Color.FromArgb(130, 60, 52);
                AppendLog($"▶ 已开始：最快速率 {_engine.Cps:0.#} CPS（实际速率由调度自动决定），通道={_cmbChannel.Text}，" +
                          $"自动宝箱={(_engine.AutoChest ? "开" : "关")}");
            }
            else
            {
                _engine.WantRun = false;
                _btnStart.Text = "开始连点";
                _btnStart.Base = Accent; _btnStart.Hover = AccentDark;
                _btnStart.Press = Color.FromArgb(136, 73, 36);
                AppendLog("⏸ 已停止");
            }
        }

        /// <summary>命令行 --start：窗体加载后自动开始连点。</summary>
        public void AutoStart()
        {
            Shown += (s, e) => BtnStart_Click(this, EventArgs.Empty);
        }

        void BtnTest_Click(object sender, EventArgs e)
        {
            ApplyConfig();
            AppendLog("— 测试一次…", 4);
            AppendLog(_engine.TestClick());
        }
    }

    // ================================================================= 自绘控件
    /// <summary>头部栏：标题 + 副标题 + 右侧 WuwaTappo 检测状态（圆点）</summary>
    internal class HeaderBar : Panel
    {
        public string StatusText = "检测中…";
        public bool Ok;
        public bool Running;

        public HeaderBar()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var bg = new SolidBrush(Color.FromArgb(251, 247, 239));
                g.FillRectangle(bg, ClientRectangle);
                using var line = new Pen(Color.FromArgb(234, 223, 200));
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);

                g.DrawString("WuwaTappo 连点器", new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
                    new SolidBrush(Color.FromArgb(107, 66, 38)), new PointF(8, 16));
                g.DrawString("次数识别 · 倒计时智能调度 · 宝箱立即开启 · 不占用鼠标",
                    new Font("Microsoft YaHei UI", 9F),
                    new SolidBrush(Color.FromArgb(156, 139, 117)), new PointF(10, 56));

                // 右侧状态：圆点 + 文字
                var f = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
                float tw = g.MeasureString(StatusText, f).Width;
                int dotX = Math.Max(220, Width - 28 - (int)tw);
                using var dot = new SolidBrush(Ok ? Color.FromArgb(106, 176, 96) : Color.FromArgb(205, 120, 100));
                g.FillEllipse(dot, dotX, 32, 12, 12);
                if (Running)
                {
                    using var ring = new Pen(Color.FromArgb(185, 106, 60), 2F);
                    g.DrawEllipse(ring, dotX - 4, 28, 20, 20);
                }
                g.DrawString(StatusText, f, new SolidBrush(Color.FromArgb(107, 66, 38)),
                    new PointF(dotX + 18, 28));
            };
        }

        public void SetStatus(string text, bool ok)
        {
            if (StatusText == text && Ok == ok) return;
            StatusText = text; Ok = ok;
            Invalidate();
        }
    }

    /// <summary>状态卡：白色圆角底 + 描边，上标题下数值（Dock=Fill 随窗口缩放）</summary>
    internal class StatCard : Panel
    {
        public string Caption = "";
        string _value = "—";
        Color _valueColor = Color.FromArgb(74, 52, 35);
        // 必须是属性并在变化时 Invalidate：UI 定时器每 100ms 只改字段不重绘的话，
        // 卡片会停留在旧画面上（曾致卡片数字滞后状态行十几秒、启动后长时间不显示）
        public string Value
        {
            get => _value;
            set { if (_value != value) { _value = value; Invalidate(); } }
        }
        public Color ValueColor
        {
            get => _valueColor;
            set { if (_valueColor != value) { _valueColor = value; Invalidate(); } }
        }

        public StatCard()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = Gfx.Round(r, 12);
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(Color.FromArgb(232, 220, 198))) g.DrawPath(p, path);

                g.DrawString(Caption, new Font("Microsoft YaHei UI", 8.25F),
                    new SolidBrush(Color.FromArgb(156, 139, 117)), new PointF(14, 10));
                var f = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
                var sz = g.MeasureString(Value, f);
                g.DrawString(Value, f, new SolidBrush(ValueColor),
                    new PointF(14, Height - sz.Height - 9));
            };
        }
    }

    /// <summary>白色圆角面板</summary>
    internal class RoundedPanel : Panel
    {
        public Color BorderColor = Color.FromArgb(232, 220, 198);
        public RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Gfx.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12);
                using var p = new Pen(BorderColor);
                g.DrawPath(p, path);
            };
        }
    }

    /// <summary>圆角按钮（焦糖橙主按钮 / 米色次按钮）</summary>
    internal class RoundedButton : Control
    {
        public Color Base = Color.FromArgb(185, 106, 60);
        public Color Hover = Color.FromArgb(158, 87, 45);
        public Color Press = Color.FromArgb(136, 73, 36);
        bool _hot, _down;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Gfx.Round(r, 12);
            Color c = !Enabled ? Color.FromArgb(210, 202, 188) : _down ? Press : _hot ? Hover : Base;
            using (var b = new SolidBrush(c)) g.FillPath(b, path);
            var textColor = Enabled ? (ForeColor == Color.FromArgb(0, 0, 0) ? Color.White : ForeColor)
                                    : Color.FromArgb(150, 140, 125);
            var f = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            var sz = g.MeasureString(Text, f);
            g.DrawString(Text, f, new SolidBrush(textColor),
                new PointF((Width - sz.Width) / 2f, (Height - sz.Height) / 2f));
        }
    }

    static class Gfx
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
