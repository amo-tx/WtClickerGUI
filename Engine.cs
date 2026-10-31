using System;
using System.Collections.Generic;
using System.Threading;

namespace WtClickerGUI
{
    /// <summary>
    /// 连点引擎（2026-10-30 联机多桌宠版）：
    /// - 计数：PostMessage → DesktopCatInput / 主窗 Tauri Window（实测均计入，位置无关）；
    ///   空白点注入，不触碰桌宠本体；每次点击前校验空白点未被桌宠占据（防拖拽）。
    /// - 视觉：PrintWindow 亮度掩码聚块（已修复邻域步长错位导致的碎片化）。
    /// - 宝箱：橙色簇预筛 + 模板匹配（ChestDetector），与桌宠连通也能检出，天然支持多桌宠；
    ///   连续 2 轮确认后经通道轮试点击，以模板消失判定开启。
    /// - 拖动防护：大块快速移动（用户拖桌宠/桌宠走动）期间暂停宝箱动作，避免把点击
    ///   打在移动中的桌宠上；桌面稳定后才恢复。
    /// </summary>
    internal class ClickerEngine
    {
        // ---- 配置（UI 修改）
        public volatile bool WantRun;
        /// <summary>用户设定的最高速率上限（2026-10-31 起 UI 为数值框）。
        /// 实际速率由程序按画面实时决定：气泡排队期匀速补门槛（可低于上限），
        /// 其余全速 = 本上限。上限只是天花板，不再直接等于点击速率。</summary>
        public double Cps = 5.0;
        public bool Humanize = true;
        /// <summary>气泡期匀速模式（借鉴 WuwaTappoClicker 的 892.5ms 恒节奏）：把门槛缺口
        /// 按剩余冷却时间摊开成低速匀速点击，替代「全速冲线后死停」。宝箱产出速率与全速
        /// 方案相同（冷却才是瓶颈），但点击更平滑、不突发，且与用户手动操作共存更友好。</summary>
        public bool PacedBubble = true;
        public bool AutoChest = true;
        public int Channel = 0;         // 0=自动 1=DesktopCatInput 2=主窗

        // ---- 回调（引擎线程调用，UI 侧自行 marshal）
        public Action<string> Log;

        Thread _thread;
        Thread _visThread;               // 视觉独立线程：截图/OCR 不再阻塞点击节奏
        volatile bool _stop;
        public bool Started;
        readonly object _vsync = new();  // 视觉共享状态锁（基线/宝箱候选/空白点/主块）

        IntPtr _inputHwnd, _mainHwnd, _injectHwnd, _rwhHwnd;
        string _injectDesc = "";
        Blob _pet;                       // 最大可见块（状态显示用）
        int _clicks, _chests;
        double _nextChestScan, _chestIgnoreUntil, _nextMacro, _nextVisScan;
        Thread _chestThread;
        double _nextHeartbeat;
        int _microLeft;
        readonly Random _rng = new Random();

        // ---- 宝箱策略（10-31 修正）：每消耗 1000 计数得 1 宝箱；连续 2 个后
        //      第 3 个起变为倒计时气泡。气泡 = 门槛已攒满、在排队冷却 → 气泡期间
        //      暂停点击防计数溢出；宝箱图标出现仍立即点击（最高优先级，消耗 1000）
        int _countX0, _countY0;
        bool _countRectValid;

        // ---- 视觉：基线 + 宝箱候选 + 运动状态
        class Tracked { public Blob B; public int Missed; public double LostAt; }   // LostAt：进丢失缓冲的时刻
        class ChestCand { public double Cx, Cy; public double Mad; public int Seen; }
        readonly List<Tracked> _baseline = new();
        readonly List<Tracked> _lostPets = new();   // 近期丢失块缓冲（联机桌宠拖动后按尺寸/位置重新关联，防删建刷屏）
        readonly List<ChestCand> _chestCands = new();
        bool _visionInit;
        List<Blob> _lastBlobs = new();
        Frame _frame;
        double _motionUntil;             // 大块运动中 → 此时间前不做宝箱动作

        // ---- 倒计时读取（应用内「总是展示宝箱」的 mm:ss 气泡；最多 2 个并行）
        double _cdRefreshEpoch;          // 最早一个气泡的刷新时刻
        readonly List<double> _cdEpochs = new();   // 全部气泡的刷新时刻（由跟踪列表重建，升序）
        double _cdSeenAt = -999;
        bool _cdWasActive;
        bool _cdAllGoneLogged;

        /// <summary>倒计时气泡跨帧跟踪：防止单帧漏读/误读让倒计时跳变
        /// （曾见 06:03 → 07:46 跳升 106s = 气泡 A 单帧漏读后最小值跳到 B）</summary>
        class CdTrack { public double Cx, Cy; public double Epoch; public int Anom; public double LastSeen; }
        readonly List<CdTrack> _cdTracks = new();

        // ---- 次数胶囊读取（桌宠下方数字）：仅用于 UI 展示，不参与任何调度决策。
        //      原因：用户键鼠操作必然随时增加计数，任何「预期值校验」都会误判；
        //      而决策也不需要它——宝箱图标弹出本身就是「攒满 1000」的信号。
        long _countVal = -1;             // 最近一次视觉读数（-1 = 尚未读到）
        double _countSeenAt = -999;
        int _countLost;                  // 位置连续失锁轮数（0.5s/轮）
        double _effCps;                  // 计划 CPS（展示用）

        // ---- 实际点击速率（UI 实时速率卡）：注入时刻滑窗统计
        readonly List<double> _clickTimes = new();
        const double CpsWindow = 4.0;    // 4s 滑窗（兼顾响应速度与平稳）

        // ---- 计数验证
        CountProbe _probe;
        double _probeNext, _lastHashChange;
        int _clicksNoCount;
        bool _countConfirmed;
        double _blankX = -1, _blankY;

        public void Start()
        {
            _stop = false;
            // 防休眠/锁屏（借鉴 WuwaTappoClicker）：挂机期间系统不睡、屏幕不灭
            Native.SetThreadExecutionState(
                Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
            Log?.Invoke("▶ 引擎线程已启动（视觉独立线程化：截图/OCR 与点击并行，实际点击速率≈设定 CPS；已阻止系统休眠/熄屏）");
            _thread = new Thread(Run) { IsBackground = true };
            _visThread = new Thread(VisLoop) { IsBackground = true };
            _chestThread = new Thread(ChestLoop) { IsBackground = true };
            _thread.Start();
            _visThread.Start();
            _chestThread.Start();
        }

        public void Shutdown()
        {
            _stop = true;
            WantRun = false;
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);   // 恢复系统电源策略
            try { _thread?.Join(1200); } catch { }
            try { _visThread?.Join(1200); } catch { }
            try { _chestThread?.Join(1200); } catch { }
        }

        /// <summary>
        /// 宝箱专用线程（0.2s 巡检，独立于点击节奏）。
        /// 之前宝箱检测寄生在点击线程循环里，匀速模式下点击间隔被拉长到 1~4s，
        /// 宝箱出现最坏要 4s 才被点——严重违背「弹出即点」最高优先级。现彻底解耦。
        /// 0.3→0.2s（2026-10-31 零空窗期）：气泡弹出→宝箱图标出现的最坏确认延迟 0.4s。
        /// </summary>
        void ChestLoop()
        {
            while (!_stop)
            {
                if (!WantRun || !AutoChest) { Thread.Sleep(250); continue; }
                if (Now() < _nextChestScan) { Thread.Sleep(40); continue; }
                _nextChestScan = Now() + 0.2;
                try { ScanChest(); } catch { }   // 单次失败忽略，下轮重试
            }
        }

        // ---------------------------------------------------------------- 主循环
        void Run()
        {
            _microLeft = Rng(50, 100);
            _nextMacro = Now() + RngD(10 * 60, 18 * 60);
            while (!_stop)
            {
                if (!WantRun) { Thread.Sleep(200); continue; }

                // ---- 窗口与注入通道：缓存句柄，仅在失效或每 3s 才重新枚举。
                //      逐点击 RefreshWindows（Process.GetProcessesByName×2 + EnumWindows×2）
                //      实测吃掉 ~100ms/次，是「计划 5 CPS 实际只有 3.2」的主因。
                bool windowsOk = _injectHwnd != IntPtr.Zero && Native.IsWindow(_injectHwnd)
                              && _mainHwnd != IntPtr.Zero && Native.IsWindow(_mainHwnd);
                if (!windowsOk || Now() >= _nextWinRefresh)
                {
                    if (!RefreshWindows())
                    {
                        EmitStatus("未检测到 WuwaTappo（请先启动 Wuwa Tappo）");
                        Thread.Sleep(3000);
                        continue;
                    }
                    _nextWinRefresh = Now() + 3;
                }
                ResolveChannel();
                if (_injectHwnd == IntPtr.Zero)
                {
                    EmitStatus("未找到可注入的窗口通道");
                    Thread.Sleep(3000);
                    continue;
                }

                // ---- 视觉扫描已移至独立线程 VisLoop（0.5s 一帧），点击线程不再被截图/OCR 阻塞

                // ---- 智能调度：气泡冷却期按倒计时降速，其余全速
                var (wait, planDesc) = PlanWait();
                StatusLine = BuildStatus(planDesc);   // 实时状态行（UI 卡片 100ms 同步显示）

                // ---- 心跳（5s 一条落盘留档；实时内容在 UI 状态行 0.1s 刷新持续可见）
                if (Now() >= _nextHeartbeat)
                {
                    _nextHeartbeat = Now() + 5;
                    Heartbeat(planDesc);
                }

                // ---- 宝箱由独立线程 ChestLoop 巡检（0.3s，弹出即点），点击线程不再负责

                while (wait > 0 && WantRun && !_stop)
                {
                    EmitStatus(planDesc);
                    SleepInterruptible(Math.Min(wait, 5));
                    if (!WantRun || _stop) break;
                    StatusLine = BuildStatus(planDesc);
                    if (Now() >= _nextHeartbeat) { _nextHeartbeat = Now() + 5; Heartbeat(planDesc); }
                    (wait, planDesc) = PlanWait();
                    StatusLine = BuildStatus(planDesc);
                }
                if (!WantRun || _stop) continue;

                // ---- 拟人化：长休息（气泡激活且刷新临近时跳过，避免休息错过宝箱弹出）
                bool cdUrgent;
                lock (_sync) cdUrgent = _cdWasActive && Now() - _cdSeenAt < 6
                                        && _cdRefreshEpoch - Now() < 300;
                // 无气泡排队 = 正在全速攒第一个 1000（用户定义的空窗期）——长休息跳过，
                // 顺延到有气泡排队的银行期再休（银行期计数富余，休 30-60s 不影响秒开）。
                bool noQueue = !cdUrgent && !CdActive(Now());
                if (Humanize && Now() > _nextMacro && !cdUrgent && !noQueue)
                {
                    var pause = RngD(30, 60);
                    EmitStatus($"长休息 {pause:F0}s（防异常检测）");
                    SleepInterruptible(pause);
                    _nextMacro = Now() + RngD(10 * 60, 18 * 60);
                    continue;
                }

                // ---- 点击空白点（计数与位置无关；不触碰桌宠防拖拽）
                // 抢鼠标防护：用户正按住物理左键（拖桌宠/拖其他东西）时，注入的
                // WM_MOUSEMOVE/DOWN 会被应用当成拖拽事件 → 桌宠漂移。按住期间完全停手。
                if (Native.LeftButtonDown)
                {
                    EmitStatus("检测到按住鼠标（拖动桌宠？），暂停注入防抢鼠标…");
                    Thread.Sleep(150);
                    continue;
                }
                double bx, by; bool occupied;
                lock (_vsync) { bx = _blankX; by = _blankY; occupied = BlankOccupiedUnlocked(bx, by); }
                if (bx < 0)
                {
                    EmitStatus("视觉扫描中（未获得可用空白点）…");
                    Thread.Sleep(500);
                    continue;
                }
                // 空白点占用校验：桌宠移到空白点附近时先让路（防把点击打在桌宠上）
                if (occupied)
                {
                    EmitStatus("空白点被桌宠占据，等待下一次视觉扫描重新选点…");
                    Thread.Sleep(400);
                    continue;
                }
                int jx = (int)bx + Rng(-6, 6);
                int jy = (int)by + Rng(-6, 6);
                try { PostCount(jx, jy); }
                catch (Exception ex) { Log?.Invoke("注入失败: " + ex.Message); Thread.Sleep(1500); continue; }
                _clicks++;
                lock (_sync)
                {
                    _clickTimes.Add(Now());
                    if (_clickTimes.Count > 600) _clickTimes.RemoveRange(0, 300);   // 30CPS×4s=120 足够，硬上限防泄漏
                }
                if (!_countConfirmed) _clicksNoCount++;

                // ---- 计数验证（每 10s 查一次计数文件哈希；文件周期性落盘）
                if (_probe != null && _probe.Available && Now() >= _probeNext)
                {
                    _probeNext = Now() + 10;
                    if (_probe.PollChanged())
                    {
                        _lastHashChange = Now();
                        if (!_countConfirmed)
                            Log?.Invoke($"✅ 计数文件已变化：注入点击计入累计（已注入 {_clicks} 次）");
                        _countConfirmed = true;
                        _clicksNoCount = 0;
                    }
                    else if (!_countConfirmed && _clicksNoCount >= 200 && Now() - _lastHashChange > 180)
                    {
                        Log?.Invoke("⚠️ 长时间注入未见计数文件更新——请检查 WuwaTappo 是否再次更新，" +
                                    "或尝试在设置里切换注入通道。");
                        _clicksNoCount = 0;
                        _lastHashChange = Now();
                    }
                }

                // ---- 拟人化：微休息
                if (Humanize)
                {
                    _microLeft--;
                    if (_microLeft <= 0)
                    {
                        EmitStatus($"微休息（已点 {_clicks}）");
                        SleepInterruptible(RngD(1.5, 3));   // 2~4s→1.5~3s：全速积攒期每次微休息都是纯空窗，收紧但不取消（保留拟人节奏）
                        _microLeft = Rng(50, 100);
                    }
                }

                // 宝箱检测已移至独立线程 ChestLoop（弹出即点，不受点击节奏影响）

                EmitStatus(null);

                // ---- 点击间隔（±40% 抖动；CPS 用智能调度的计划值）
                SleepInterruptible(1.0 / Math.Max(0.3, PlanCps) * RngD(0.6, 1.4));
            }
        }

        /// <summary>
        /// 智能调度（10-31 23:58 修订）：关键实测——开箱 = 计数扣 1000、余数结转
        /// （10-31 23:54 开箱前 1,087+3 次注入，开箱后 91 ≈ 1090−1000+1）。
        /// 因此「到门槛就暂停防溢出」是错误前提：多点的计数不浪费，只会让
        /// 下一次积攒更短、并保证气泡成熟宝箱弹出时计数必 ≥ 1000 秒开。
        /// 排队气泡数 = 屏幕上真实可见的倒计时气泡数（3s 内读到即有效）。
        /// 策略：
        ///   ① 宝箱图标出现 → 最高优先级立即点击（独立线程 0.2s 巡检）；
        ///   ② 有气泡排队 → 持续全速积攒（余数结转，不再到门槛就停）；
        ///   ③ 仅当计数接近疑似上限（≥9000）才保守停手，防游戏内部截断；
        ///   ④ 无气泡 → 全速攒下一个 1000。
        /// </summary>
        (double wait, string desc) PlanWait()
        {
            double now = Now();
            bool cdActive = CdActive(now);
            if (cdActive)
            {
                int queued;
                lock (_sync) queued = Math.Max(1, _cdEpochs.Count);
                double rem0 = Math.Max(0, _cdRefreshEpoch - now);
                long v, v30;
                lock (_sync)
                {
                    double age = now - _countSeenAt;
                    v = age < 5 ? _countVal : -1;
                    v30 = age < 30 ? _countVal : -1;
                }
                // 保守上限护栏：计数极端偏高时停手（游戏内部是否有截断未知，宁停勿险）。
                // 正常 5 CPS 攒到 9000 需 ~30 分钟，实际永远碰不到；只是兜底。
                if (v30 >= 9000)
                {
                    Paused = true;
                    _effCps = 0;
                    return (0.4, $"策略=保守暂停：计数 {v30:N0} 已近疑似上限 9000（防游戏内部截断），" +
                                 $"{queued} 个气泡排队冷却 {TimeSpan.FromSeconds(rem0):mm\\:ss}，气泡成熟宝箱弹出即自动点击");
                }
                // 「匀速补门槛」已停用（10-31 23:58）：其实测前提（到门槛就该停）
                // 被开箱余数结转推翻——匀速把积攒拖到 0.25~1 CPS 爬行 30 分钟，
                // 是空窗期制造机。PacedBubble 配置键保留仅为兼容旧 config.ini。
                Paused = false;
                _effCps = Cps;
                return (0, $"策略=持续积攒：{queued} 个气泡排队冷却 {TimeSpan.FromSeconds(rem0):mm\\:ss}" +
                           $"（计数{(v >= 0 ? v.ToString("N0") : "未读取")}；开箱扣 1000 余数结转，多点多算不浪费），气泡成熟宝箱弹出即秒开");
            }
            Paused = false;
            _effCps = Cps;
            return (0, null);   // 无气泡 → 全速攒下一个宝箱
        }

        /// <summary>倒计时气泡是否「实际在屏」（3s 内读到过 = 有效；无气泡即无排队，不内部推断）</summary>
        bool CdActive(double now)
        {
            lock (_sync) return _cdWasActive && now - _cdSeenAt < 3;
        }

        /// <summary>
        /// 实时状态快照（与心跳日志同格式）。
        /// 引擎运行期间每次调度都重建（点击循环 ~5Hz、暂停期 0.4s 一次），
        /// UI 线程每 300ms 读取一次同步到状态行/卡片 —— 心跳内容实时可见，不再等 15s。
        /// </summary>
        public volatile string StatusLine = "";

        string BuildStatus(string planDesc)
        {
            double now = Now();
            bool cdA = CdActive(now);
            string vTxt;
            lock (_sync)
            {
                vTxt = _countVal < 0 ? "未读取"
                    : _countVal.ToString("N0") + (now - _countSeenAt < 5 ? "" : "（已过期）");
            }
            string needTxt = Paused
                ? $"暂停点击（{QueuedBubbles} 个排队）"
                : $"还需={NeededClicks}{(now - _countSeenAt < 5 ? "" : "（基于过期读数，仅供显示）")}";
            return $"次数={vTxt} {needTxt} " +
                   $"倒计时={(cdA ? $"{Math.Max(0, _cdRefreshEpoch - now):F0}s" : "无")} " +
                   $"速率={_effCps:F1}→{ActualCps:F1}/s 已注入={_clicks} 宝箱={_chests} | {planDesc ?? "全速连点"}";
        }

        void Heartbeat(string planDesc)
        {
            Log?.Invoke("心跳: " + BuildStatus(planDesc));
        }

        // ---------------------------------------------------------------- 视觉
        /// <summary>
        /// 待机时由 UI 线程调用（UI 每 300ms tick 一次，本方法内部节流 ~0.5s）：
        /// 扫一帧读「次数 + 倒计时」，让状态卡在未开始连点时也实时跟随桌宠数据。
        /// 返回可见块数量（-1 = WuwaTappo 不在线 / 抓帧失败），供头部状态显示。
        /// 引擎运行时由引擎线程自己读（本方法直接返回 -2 不做重复扫描）。
        /// </summary>
        public int ReadIdleVision()
        {
            if (WantRun || _stop) return -2;
            double now = Now();
            if (now < _idleNext) return IdleBlobCount;   // 节流：0.5s 内复用上次结果
            _idleNext = now + 0.5;
            try
            {
                var main = _mainHwnd != IntPtr.Zero && Native.IsWindow(_mainHwnd)
                    ? _mainHwnd : Scanner.FindMainWindow();
                if (main == IntPtr.Zero) { IdleBlobCount = -1; return -1; }
                _mainHwnd = main;
                var (frame, blobs) = Scanner.ScanFrame(main);
                if (frame == null) { IdleBlobCount = -1; return -1; }
                lock (_sync) UpdateReadings(frame);
                IdleBlobCount = blobs.Count;
                return blobs.Count;
            }
            catch { return -1; }
        }

        /// <summary>最近一次扫描的可见块数量（-1 = 不在线，-2 = 引擎自扫中）。</summary>
        public volatile int IdleBlobCount = -2;
        double _idleNext;                // 待机视觉节流（0.5s）
        double _nextWinRefresh;          // 窗口句柄缓存刷新节拍（3s，点击循环提速）
        readonly object _sync = new();

        /// <summary>
        /// 倒计时气泡 + 次数胶囊读取（引擎线程与 UI 待机轮询共用）。
        /// 共享状态在 _sync 锁内更新。调用方需已持有帧。
        /// </summary>
        void UpdateReadings(Frame frame)
        {
            // ---- 倒计时气泡读取（连续 2 个宝箱后的冷却 mm:ss 气泡，最多 2 个）
            //      跨帧跟踪 + 异常值拒绝：按位置把读数关联到已跟踪气泡，
            //      单帧漏读（跳变到另一气泡）或误读（模板失配）不改变跟踪值；
            //      连续 4 帧（2s）偏差才接受修正。气泡消失按 8s 无读数判定。
            var cds = CountdownReader.Find(frame);
            double cdNow = Now();
            foreach (var c in cds)
            {
                double cx = (c.X0 + c.X1) / 2.0, cy = (c.Y0 + c.Y1) / 2.0;
                CdTrack t = null; double bd = double.MaxValue;
                foreach (var k in _cdTracks)
                {
                    double d = Math.Abs(k.Cx - cx) + Math.Abs(k.Cy - cy);
                    if (d < bd) { bd = d; t = k; }
                }
                if (t == null || bd > 80)
                {
                    if (_cdWasActive)
                        Log?.Invoke($"新倒计时气泡出现 @({cx:F0},{cy:F0})，剩余 {c.Seconds / 60}:{c.Seconds % 60:D2}，加入跟踪");
                    _cdTracks.Add(new CdTrack { Cx = cx, Cy = cy, Epoch = cdNow + c.Seconds, LastSeen = cdNow });
                    continue;
                }
                double expected = t.Epoch - cdNow;
                if (Math.Abs(c.Seconds - expected) <= 4 || expected <= 0)
                {
                    t.Epoch = cdNow + c.Seconds; t.Anom = 0;
                }
                else if (++t.Anom >= 4)
                {
                    Log?.Invoke($"倒计时气泡读数与跟踪值偏差过大（跟踪剩余 {Math.Max(0, expected):F0}s，读到 {c.Seconds}s，" +
                                "连续 4 帧）→ 接受新读数");
                    t.Epoch = cdNow + c.Seconds; t.Anom = 0;
                }
                // 其余情况：单帧异常（漏读跳到邻近气泡/模板误读），丢弃本帧读数，保留跟踪值
                t.Cx = cx; t.Cy = cy; t.LastSeen = cdNow;
            }
            for (int i = _cdTracks.Count - 1; i >= 0; i--)
                if (cdNow - _cdTracks[i].LastSeen > 8)
                {
                    Log?.Invoke($"一个倒计时气泡已消失（还剩 {_cdTracks.Count - 1} 个在跟踪）");
                    _cdTracks.RemoveAt(i);
                }
            _cdEpochs.Clear();
            foreach (var t in _cdTracks) _cdEpochs.Add(t.Epoch);
            _cdEpochs.Sort();
            if (_cdTracks.Count > 0)
            {
                _cdRefreshEpoch = _cdEpochs[0];
                _cdSeenAt = cdNow;
                if (!_cdWasActive)
                    Log?.Invoke($"读到宝箱刷新倒计时 {(int)Math.Max(0, _cdEpochs[0] - cdNow) / 60}:" +
                                $"{(int)Math.Max(0, _cdEpochs[0] - cdNow) % 60:D2}（共 {_cdTracks.Count} 个气泡），将按倒计时动态调度。");
                _cdWasActive = true;
                _cdAllGoneLogged = false;
            }
            else if (_cdWasActive && cdNow - _cdSeenAt > 8 && !_cdAllGoneLogged)
            {
                _cdWasActive = false;
                _cdAllGoneLogged = true;
                Log?.Invoke("倒计时气泡全部消失 → 后续宝箱将直接弹出，恢复正常全速连点。");
            }

            // ---- 次数胶囊读取（只有本机桌宠有）：按位置连续性跟踪，
            //      过滤访客名牌数字等误检；位置突变连续 3s（桌宠被拖走）则
            //      重新捕获，避免死锁在旧位置导致卡片永远「—」。
            //      读数展示与恢复调度参考；气泡排队期间调度不依赖此值。
            List<CounterReader.CounterRead> cs;
            try { cs = CounterReader.Find(frame); }
            catch (Exception ex)
            {
                // 不再静默吞掉：2026-10-31 晚「离线同帧可读、引擎必失败」的排障表明
                // VisLoop 的 catch{} 会把读取器异常全部掩盖（倒计时在前所以正常、
                // 次数在后永远未读取）。异常必须留痕。
                Log?.Invoke($"⚠️ 次数读取异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                cs = new List<CounterReader.CounterRead>();
            }
            if (cs.Count == 0)
                DumpFailFrame(frame);
            if (cs.Count > 0)
            {
                CounterReader.CounterRead best = null;
                if (_countRectValid)
                {
                    double bd = double.MaxValue;
                    foreach (var c in cs)
                    {
                        double d = Math.Abs(c.X0 - _countX0) + Math.Abs(c.Y0 - _countY0);
                        if (d < bd) { bd = d; best = c; }
                    }
                    if (bd > 150)
                    {
                        // 位置突变：疑似误检或桌宠移动。连续失锁 3s → 桌宠已移走，重新捕获
                        if (++_countLost >= 6)
                        {
                            _countRectValid = false;
                            _countLost = 0;
                            Log?.Invoke("计数胶囊位置连续失锁 3s（桌宠已移动？），重新捕获。");
                        }
                        best = null;
                    }
                    else _countLost = 0;
                }
                else
                {
                    // 重新捕获：优先位数最多的读数；≥2 位才采信 —— 名牌笔画偶发
                    // 匹配出单数字假读（如「8」），真计数在连点下 1s 内必然回到 2 位以上
                    best = cs[0];
                    foreach (var c in cs) if (c.Digits > best.Digits) best = c;
                    if (best.Digits < 2) best = null;
                }
                if (best != null)
                {
                    _countX0 = best.X0; _countY0 = best.Y0; _countRectValid = true;
                    // 纯展示，零校验（用户裁决：内置计数逻辑完全舍去）。
                    // v4 读取器的质量控制在读取端完成（✦ 结构锚点 + 模板 MAD 门槛），
                    // 偶尔单帧失败会保留旧值并过期标记，0.5s 后自愈。
                    // 注意：不可按「位数骤减」过滤 —— 领宝箱计数归零（1005→3）时
                    // 位数 4→1 而胶囊区域不变，任何此类护栏都会永久拒绝合法复位。
                    bool first = _countVal < 0;
                    _countVal = best.Value;
                    _countSeenAt = Now();
                    if (first) Log?.Invoke($"✅ 已读取桌宠计数：{best.Value}（实时读数；气泡排队的暂停/恢复门槛判定以此为准，仅采信 5s 内的新鲜值）");
                }
            }
        }

        /// <summary>视觉独立线程：专职 0.5s 一帧截图+识别，点击线程完全不被阻塞。</summary>
        double _lastCtrDump = -999;
        int _ctrDumps;

        /// <summary>次数读不到时把引擎真实帧落盘（限 2 帧、间隔≥10s），供 ReaderTest 离线复现。</summary>
        void DumpFailFrame(Frame f)
        {
            if (f?.Bits == null) return;
            double now = Now();
            if (_ctrDumps >= 2 || now - _lastCtrDump < 10) return;
            _lastCtrDump = now; _ctrDumps++;
            try
            {
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, $"ctr_fail_{_ctrDumps}.bin");
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
                fs.Write(BitConverter.GetBytes(f.W), 0, 4);
                fs.Write(BitConverter.GetBytes(f.H), 0, 4);
                fs.Write(f.Bits, 0, f.Bits.Length);
                Log?.Invoke($"⚠️ 次数未读取，引擎帧已落盘 {path}（{f.W}x{f.H}）——可用 ReaderTest 离线复现");
            }
            catch (Exception ex) { Log?.Invoke("帧落盘失败: " + ex.Message); }
        }

        void VisLoop()
        {
            while (!_stop)
            {
                if (!WantRun) { Thread.Sleep(200); continue; }
                if (_mainHwnd == IntPtr.Zero || !Native.IsWindow(_mainHwnd)) { Thread.Sleep(300); continue; }
                if (Now() < _nextVisScan) { Thread.Sleep(40); continue; }
                _nextVisScan = Now() + 0.5;
                try
                {
                    UpdateVision();   // 单帧失败忽略，下轮重扫
                    // 状态行随视觉扫描重建（含倒计时剩余/CPS/策略），与点击节奏解耦，
                    // 匀速模式下也不会出现「几十秒不变」的旧状态
                    var (_, planDesc) = PlanWait();
                    StatusLine = BuildStatus(planDesc);
                }
                catch { }
            }
        }

        void UpdateVision()
        {
            var (frame, blobs) = Scanner.ScanFrame(_mainHwnd);
            if (frame == null || blobs.Count == 0) return;

            lock (_sync) UpdateReadings(frame);

            bool firstInit = false; bool probeAvail = false;
            lock (_vsync)
            {
                _frame = frame;
                _lastBlobs = blobs;
                IdleBlobCount = blobs.Count;

                if (!_visionInit)
                {
                    _visionInit = true;
                    firstInit = true;
                    foreach (var b in blobs) _baseline.Add(new Tracked { B = b, Missed = 0 });
                    if (_probe == null)
                    {
                        _probe = new CountProbe();
                        _probeNext = Now() + 8;
                        _lastHashChange = Now();
                    }
                }
                else
                {
                    // ---- 运动检测：大块（N>=50）相对上一轮移动 > 40px → 拖拽/走动中
                    //      仅当移动块靠近宝箱候选时才暂停宝箱动作——联机模式其他玩家
                    //      拖动/走动自己的桌宠，不应耽误本机宝箱领取（宝箱点击每击前
                    //      都会重抓帧重检测，本身就能跟上移动目标）。
                    double maxMove = 0;
                    foreach (var b in blobs)
                    {
                        if (b.N < 50) continue;
                        double best = double.MaxValue;
                        foreach (var p in _baseline)
                            if (p.B.N >= 50) best = Math.Min(best, p.B.Dist(b));
                        if (best < double.MaxValue) maxMove = Math.Max(maxMove, best);
                    }
                    if (maxMove > 40)
                    {
                        bool nearChest = false;
                        foreach (var b in blobs)
                        {
                            if (b.N < 50) continue;
                            foreach (var c in _chestCands)
                                if (Math.Abs(b.Cx - c.Cx) < 130 && Math.Abs(b.Cy - c.Cy) < 130) { nearChest = true; break; }
                            if (nearChest) break;
                        }
                        if (nearChest) _motionUntil = Now() + 2.5;   // 只有宝箱附近有块在动才暂停（静止后 2.5s 恢复）
                    }

                    // ---- 基线匹配更新（10-31 联机拖动修复）：
                    //      位置容差 90px；匹配失败时先查「近期丢失」块——联机其他桌宠
                    //      被拖动时一帧能跳 200px+，旧逻辑会把它当「消失+新出现」反复
                    //      删建并刷屏日志；现在按尺寸相近（±45%）在 400px 内重新关联，
                    //      只有真正新进入画面的块才记「新可见块」。
                    var newlySeen = new List<Blob>();
                    foreach (var b in blobs)
                    {
                        var m = _baseline.Find(t => t.B.Dist(b) < 90 && Math.Abs(t.B.N - b.N) < Math.Max(60, b.N / 3));
                        if (m == null)
                        {
                            Tracked lost = null; double ld = double.MaxValue;
                            foreach (var t in _lostPets)
                            {
                                double d = t.B.Dist(b);
                                if (d < 400 && Math.Abs(t.B.N - b.N) < Math.Max(80, b.N * 0.45) && d < ld)
                                { ld = d; lost = t; }
                            }
                            if (lost != null)
                            {
                                _lostPets.Remove(lost);
                                lost.B = b; lost.Missed = 0;
                                _baseline.Add(lost);
                                continue;
                            }
                            newlySeen.Add(b);
                            _baseline.Add(new Tracked { B = b, Missed = 0 });
                        }
                        else { m.B = b; m.Missed = 0; }
                    }
                    if (newlySeen.Count > 0)
                        foreach (var b in newlySeen) Log?.Invoke($"新可见块进入画面：{b}");
                }

                foreach (var t in _baseline) t.Missed++;
                for (int i = _baseline.Count - 1; i >= 0; i--)
                    if (_baseline[i].Missed > 10)
                    {
                        var lost = _baseline[i];
                        lost.LostAt = Now();
                        _lostPets.Add(lost);              // 进近期丢失缓冲（拖动后能重新关联）
                        if (_lostPets.Count > 8) _lostPets.RemoveAt(0);
                        _baseline.RemoveAt(i);
                    }
                _lostPets.RemoveAll(t => Now() - t.LostAt > 6);

                // ---- 宝箱：模板匹配（与桌宠连通也能检出，天然支持多桌宠）
                UpdateChestCandidates();

                _pet = PickPrimary(blobs);
                SetBlankPoint(blobs);

                if (firstInit && _probe != null) { probeAvail = _probe.Available; }
            }

            if (firstInit)
            {
                Blob pet; double bxx;
                lock (_vsync) { pet = _pet; bxx = _blankX; }
                Log?.Invoke($"视觉扫描就绪：{blobs.Count} 个可见块，主块 {pet}（通道 {_injectDesc}）");
                Log?.Invoke($"点击模式: 空白点({bxx:F0},{_blankY:F0})" +
                            (probeAvail ? "；将自动验证计数" : "；⚠️ 未找到计数文件，无法自动验证"));
            }
        }

        static Blob PickPrimary(List<Blob> blobs)
        {
            Blob best = null;
            foreach (var b in blobs)
                if (b.N >= 4 && (best == null || b.N > best.N)) best = b;
            return best;
        }

        // ---- 供 UI 轮询的运行状态（与引擎线程/UI 待机轮询共享 → 锁内读取）
        public long CountVal { get { lock (_sync) return _countVal; } }
        public bool CountFresh { get { lock (_sync) return Now() - _countSeenAt < 10; } }
        /// <summary>气泡排队冷却期（次数已攒满，暂停点击防溢出）</summary>
        public bool Paused { get; private set; }
        /// <summary>当前排队冷却的宝箱数（屏幕上实际可见的倒计时气泡数，0 = 无排队）</summary>
        public int QueuedBubbles
        {
            get { lock (_sync) return (_cdWasActive && Now() - _cdSeenAt < 3) ? _cdEpochs.Count : 0; }
        }
        public int Clicks => _clicks;
        public int Chests { get { lock (_sync) return _chests; } }
        public double PlanCps => _effCps > 0 ? _effCps : Cps;
        /// <summary>实际点击速率（次/秒，4s 滑窗）：UI「实时点击速率」卡与状态行使用。
        /// 与计划速率的区别：暂停/宝箱点击/微休息期间为真实值（趋 0），计划值不变。</summary>
        public double ActualCps
        {
            get
            {
                double now = Now();
                int n;
                lock (_sync)
                {
                    _clickTimes.RemoveAll(t => now - t > CpsWindow);
                    n = _clickTimes.Count;
                }
                return n / CpsWindow;
            }
        }
        public string LiveDesc = "";
        /// <summary>距下一宝箱还需的点击次数（-1 = 次数未读取；0 = 已攒够 1000）</summary>
        public int NeededClicks
        {
            get
            {
                lock (_sync)
                {
                    if (_countVal < 0) return -1;
                    if (_countVal == 0) return 1000;
                    long m = _countVal % 1000;
                    return m == 0 ? 0 : (int)(1000 - m);
                }
            }
        }
        /// <summary>宝箱刷新倒计时剩余秒数（-1 = 当前无倒计时）</summary>
        public double CdRemain
        {
            get
            {
                lock (_sync)
                {
            double now = Now();
            bool a = _cdWasActive && now - _cdSeenAt < 3;
            return a ? Math.Max(0, _cdRefreshEpoch - now) : -1;
                }
            }
        }

        void UpdateChestCandidates()
        {
            if (_frame == null) return;
            var found = ChestDetector.Find(_frame.Bits, _frame.W, _frame.H);
            foreach (var (cx, cy, mad) in found)
            {
                double sx = _frame.Left + cx, sy = _frame.Top + cy;
                var c = _chestCands.Find(t => Math.Abs(t.Cx - sx) < 45 && Math.Abs(t.Cy - sy) < 45);
                if (c != null) { c.Seen++; c.Mad = Math.Min(c.Mad, mad); }
                else
                {
                    _chestCands.Add(new ChestCand { Cx = sx, Cy = sy, Mad = mad, Seen = 1 });
                    Log?.Invoke($"宝箱候选出现 @({sx:F0},{sy:F0}) MAD={mad:F1}，等待第 2 轮确认…");
                }
            }
            // 本轮未检出的候选移除（宝箱被点掉/消失）
            _chestCands.RemoveAll(c => !found.Exists(f =>
                Math.Abs(_frame.Left + f.Cx - c.Cx) < 45 && Math.Abs(_frame.Top + f.Cy - c.Cy) < 45));
        }

        // ---------------------------------------------------------------- 宝箱
        void ScanChest()
        {
            if (Now() < _chestIgnoreUntil) return;
            if (Now() < _motionUntil) return;          // 拖拽/走动中不动宝箱

            ChestCand target = null;
            lock (_vsync)
            {
                foreach (var c in _chestCands)
                {
                    // 确认门槛（零空窗期 2026-10-31）：
                    //   常规 = 连续 2 轮（0.2s 巡检 → 最坏 0.4s）；
                    //   快道 = 单轮但 MAD<10 —— 掩码 MAD 实测真宝箱 0~9、干扰簇 47+，
                    //          高置信单帧直接点，气泡弹出→开箱最坏延迟从 ~0.9s 压到 ~0.2s。
                    bool confirmed = c.Seen >= 2 || (c.Seen >= 1 && c.Mad < 10);
                    if (confirmed && (target == null || c.Mad < target.Mad)) target = c;
                }
            }
            if (target == null) return;

            // 零空窗期（双宝箱版 2026-10-31 23:00）：同一轮巡检内把所有已确认宝箱
            // 逐个开完——开完 A 立即选 B，不在两箱之间插 1s 静默。
            // 旧流程「A 消失确认 → 1s 静默 → 下轮巡检才选 B」让并存的 B 白等 ~2s。
            int openedThisPass = 0;
            while (WantRun && !_stop)
            {
            target = null;
            lock (_vsync)
            {
                foreach (var c in _chestCands)
                {
                    bool confirmed = c.Seen >= 2 || (c.Seen >= 1 && c.Mad < 10);
                    if (confirmed && (target == null || c.Mad < target.Mad)) target = c;
                }
            }
            if (target == null) break;

            Log?.Invoke($"检测到宝箱 @({target.Cx:F0},{target.Cy:F0})（连续 {target.Seen} 轮确认，MAD={target.Mad:F1}），点击开启…");
            EmitStatus("点击宝箱中…");

            var chans = ChestChannels();
            if (chans.Count == 0)
            {
                EmitStatus("未找到任何宝箱点击通道，30s 后重试");
                _chestIgnoreUntil = Now() + 30;
                return;
            }

            bool gone = false;
            for (int attempt = 0; attempt < 9 && !gone && WantRun && !_stop; attempt++)
            {
                // 抢鼠标防护：用户按住左键（拖桌宠）期间不注入，稍后重试
                if (Native.LeftButtonDown) { Thread.Sleep(400); continue; }
                // 每击前重抓帧重检测（桌宠移动也跟得上；宝箱消失立即停手）。
                // 点击后 350~550ms 即重扫：宝箱收到点击即进入开启动画并消失，
                // 早确认早转下一个宝箱（原 900~1200ms 每箱多耗 ~0.7s，两箱并存时空窗更明显）。
                var (frame, _) = Scanner.ScanFrame(_mainHwnd);
                if (frame != null)
                {
                    var found = ChestDetector.Find(frame.Bits, frame.W, frame.H);
                    (double, double) cur = (double.NaN, double.NaN);
                    foreach (var (cx, cy, mad) in found)
                    {
                        double sx = frame.Left + cx, sy = frame.Top + cy;
                        if (Math.Abs(sx - target.Cx) < 60 && Math.Abs(sy - target.Cy) < 60) { cur = (sx, sy); break; }
                    }
                    if (double.IsNaN(cur.Item1)) { gone = true; break; }
                    target.Cx = cur.Item1; target.Cy = cur.Item2;   // 跟随移动
                    var (desc, hwnd) = chans[attempt % chans.Count];
                    try { PostClickTo(hwnd, (int)cur.Item1 + Rng(-5, 5), (int)cur.Item2 + Rng(-5, 5)); }
                    catch (Exception ex) { Log?.Invoke($"宝箱点击失败[{desc}]: " + ex.Message); continue; }
                }
                Thread.Sleep(Rng(350, 550));
            }
            // 只移除已开启的这一个候选（≤60px），其他同时在屏的宝箱保留确认状态
            // ——整表 Clear 曾让并存宝箱重新从 0 累积确认轮数，白白多等 0.4~0.8s。
            lock (_vsync)
                _chestCands.RemoveAll(c =>
                    Math.Abs(c.Cx - target.Cx) < 60 && Math.Abs(c.Cy - target.Cy) < 60);
            if (!gone)
            {
                // 9 次点击仍在 → 冷却 30s，放弃本轮剩余宝箱（下轮巡检重来）
                Log?.Invoke("已轮试各通道点击 9 次图标仍在，30s 后重试（若持续失败请把日志发给开发者）。");
                _chestIgnoreUntil = Now() + 30;
                return;
            }

            openedThisPass++;
            _chests++;
            // 零空窗期关键一步：刚开的宝箱必来自一个已到期/即将到期的倒计时气泡。
            // 立即移除这些气泡排队（原来要等 8s 无读数才判定消失），
            // 门槛 1000×排队数马上回落，匀速/暂停策略立刻按真实排队数重算——
            // 否则这 8s 里引擎会朝一个虚高的门槛多积攒（多点的全浪费）或错误暂停。
            int removed = 0;
            double nowC = Now();
            lock (_sync)
            {
                removed = _cdTracks.RemoveAll(t => t.Epoch <= nowC + 10);
                if (removed > 0)
                {
                    _cdEpochs.Clear();
                    foreach (var t in _cdTracks) _cdEpochs.Add(t.Epoch);
                    _cdEpochs.Sort();
                    if (_cdTracks.Count > 0) _cdRefreshEpoch = _cdEpochs[0];
                    else { _cdWasActive = false; _cdSeenAt = -999; }
                }
            }
            int remainCands;
            lock (_vsync) remainCands = _chestCands.Count;
            Log?.Invoke($"✅ 宝箱已开启（本次运行已开 {_chests} 个）" +
                        (removed > 0 ? $"，移除 {removed} 个已到期气泡排队，立即转入下一段积攒" : "") +
                        (remainCands > 0 ? $"；屏上还有 {remainCands} 个宝箱，立即继续" : ""));
            // 循环继续 → 立即选取下一个已确认宝箱（无静默间隔）
            }
            if (openedThisPass > 0)
                _chestIgnoreUntil = Now() + 1;   // 全部开完后才静默 1s（新宝箱可能马上弹出）
        }

        List<(string desc, IntPtr hwnd)> ChestChannels()
        {
            var list = new List<(string, IntPtr)>();
            if (EnsureRwh()) list.Add(("RenderWidget", _rwhHwnd));
            if (_mainHwnd != IntPtr.Zero) list.Add(("主窗", _mainHwnd));
            if (_inputHwnd != IntPtr.Zero) list.Add(("DesktopCatInput", _inputHwnd));
            return list;
        }

        bool EnsureRwh()
        {
            if (_rwhHwnd != IntPtr.Zero && Native.IsWindow(_rwhHwnd)) return true;
            _rwhHwnd = Scanner.FindRwhHwnd(_mainHwnd);
            return _rwhHwnd != IntPtr.Zero;
        }

        // ---------------------------------------------------------------- 注入
        bool RefreshWindows()
        {
            _mainHwnd = Scanner.FindMainWindow();
            _inputHwnd = Scanner.FindInputHwnd();
            return _mainHwnd != IntPtr.Zero;
        }

        void ResolveChannel()
        {
            if (_injectHwnd != IntPtr.Zero && Native.IsWindow(_injectHwnd)) return;
            _injectHwnd = IntPtr.Zero; _injectDesc = "";
            var tryOrder = new List<(int ch, string desc, IntPtr h)>();
            if (Channel == 0 || Channel == 1)
                tryOrder.Add((1, "DesktopCatInput", _inputHwnd));
            if (Channel == 0 || Channel == 2)
                tryOrder.Add((2, "主窗Tauri", _mainHwnd));
            foreach (var (ch, desc, h) in tryOrder)
            {
                if (h != IntPtr.Zero && Native.IsWindow(h))
                {
                    _injectHwnd = h; _injectDesc = desc;
                    break;
                }
            }
        }

        void PostCount(int screenX, int screenY)
        {
            ResolveChannel();
            if (_injectHwnd == IntPtr.Zero) throw new InvalidOperationException("无注入通道");
            PostClickTo(_injectHwnd, screenX, screenY);
        }

        void PostClickTo(IntPtr hwnd, int screenX, int screenY)
        {
            int cx = screenX, cy = screenY;
            if (Native.GetWindowRect(hwnd, out Native.RECT r))
            {
                cx -= r.Left; cy -= r.Top;              // 客户坐标
            }
            IntPtr lp = (IntPtr)(((cy & 0x7FFF) << 16) | (cx & 0xFFFF));
            Native.PostMessage(hwnd, Native.WM_MOUSEMOVE, Native.Zero, lp);
            Thread.Sleep(8);
            Native.PostMessage(hwnd, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, lp);
            Thread.Sleep(15);
            Native.PostMessage(hwnd, Native.WM_LBUTTONUP, Native.Zero, lp);
        }

        /// <summary>供 UI「测试一次」调用：向空白点投递一次计数点击，返回描述</summary>
        public string TestClick()
        {
            if (!RefreshWindows()) return "未检测到 WuwaTappo，请先启动 Wuwa Tappo";
            ResolveChannel();
            if (_injectHwnd == IntPtr.Zero) return "未找到注入通道";
            var (frame, blobs) = Scanner.ScanFrame(_mainHwnd);
            if (frame != null && blobs.Count > 0)
            {
                lock (_vsync)
                {
                    _frame = frame; _lastBlobs = blobs;
                    _pet = PickPrimary(blobs); SetBlankPoint(blobs);
                }
            }
            double tbx, tby;
            lock (_vsync) { tbx = _blankX; tby = _blankY; }
            if (tbx < 0) return "未获得可用空白点（视觉扫描失败？）";
            try { PostCount((int)tbx, (int)tby); }
            catch (Exception ex) { return "注入失败: " + ex.Message; }
            return $"已通过 [{_injectDesc}] 向空白点 ({tbx:F0},{tby:F0}) 投递 1 次计数点击。" +
                   "空白点不触碰桌宠、无动画；计数生效与否以日志自动验证为准。";
        }

        // ---------------------------------------------------------------- 空白点
        /// <summary>空白点占用校验（调用方需持有 _vsync 锁）</summary>
        bool BlankOccupiedUnlocked(double bx, double by)
        {
            if (bx < 0) return false;
            foreach (var b in _lastBlobs)
                if (Math.Abs(bx - b.Cx) < 80 && Math.Abs(by - b.Cy) < 80) return true;
            return false;
        }

        /// <summary>选一个离所有可见块最远的屏幕点作为计数点击点</summary>
        void SetBlankPoint(List<Blob> all)
        {
            int vx = Native.GetSystemMetrics(76), vy = Native.GetSystemMetrics(77);
            int vw = Native.GetSystemMetrics(78), vh = Native.GetSystemMetrics(79);
            double bestScore = double.MinValue, bx = double.MinValue, by = 0;
            for (int y = vy + vh / 4; y <= vy + vh - 80; y += 100)
            {
                for (int x = vx + 80; x <= vx + vw - 80; x += 100)
                {
                    double minD = double.MaxValue;
                    foreach (var b in all)
                    {
                        double d = Math.Max(Math.Abs(x - b.Cx), Math.Abs(y - b.Cy));
                        if (d < minD) minD = d;
                    }
                    if (all.Count == 0) minD = 1e6;
                    double centerBias = -(Math.Abs(x - (vx + vw / 2.0)) + Math.Abs(y - (vy + vh * 0.7))) / 2000.0;
                    double score = Math.Min(minD, 500) + centerBias;
                    if (score > bestScore) { bestScore = score; bx = x; by = y; }
                }
            }
            if (bx > double.MinValue) { _blankX = bx; _blankY = by; }
        }

        // ---------------------------------------------------------------- 工具
        double Now() => Environment.TickCount64 / 1000.0;
        int Rng(int a, int b) => _rng.Next(a, b + 1);
        double RngD(double a, double b) => a + _rng.NextDouble() * (b - a);

        void SleepInterruptible(double seconds)
        {
            long end = Environment.TickCount64 + (long)(seconds * 1000);
            while (!_stop && WantRun && Environment.TickCount64 < end)
                Thread.Sleep(40);
        }

        void EmitStatus(string extra)
        {
            if (extra != null) { LiveDesc = extra; return; }
            Blob pet;
            lock (_vsync) pet = _pet;
            string p = pet == null ? "未检测" : $"({pet.Cx:F0},{pet.Cy:F0})";
            LiveDesc = $"桌宠 {p} · 通道 {_injectDesc} · {PlanCps:F1} CPS";
        }
    }
}
