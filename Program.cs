using System;
using System.Windows.Forms;

namespace WtClickerGUI
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 注意：SetProcessDPIAware 会锁定进程 DPI 感知，使后面 ApplicationConfiguration
            // 的 PerMonitorV2 失效 → 窗口在 150% 屏上被缩成 2/3、文字拥挤截断。
            // 只有 --selftest（无头抓帧需要真实物理坐标）才允许调用。
            if (args.Length > 0 && args[0] == "--selftest")
            {
                try { Native.SetProcessDPIAware(); } catch { }
                return SelfTest.Run();
            }
            ApplicationConfiguration.Initialize();
            var form = new MainForm();
            if (args.Length > 0 && args[0] == "--start")
                form.AutoStart();       // 启动即自动开始连点（免点击验证 / 挂机直达）
            Application.Run(form);
            return 0;
        }

        /// <summary>
        /// 无头自检：抓一帧 → 依次跑 次数胶囊 / 倒计时气泡 / 宝箱检测，打印结果。
        /// 用法：WtClickerGUI.exe --selftest
        /// </summary>
        internal static class SelfTest
        {
            public static int Run()
            {
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
                var main = Scanner.FindMainWindow();
                if (main == IntPtr.Zero) { Console.WriteLine("FAIL: 未找到 Wuwa Tappo 主窗"); return 1; }
                var (frame, blobs) = Scanner.ScanFrame(main);
                if (frame == null) { Console.WriteLine("FAIL: 抓帧失败"); return 1; }
                Console.WriteLine($"frame {frame.W}x{frame.H} @({frame.Left},{frame.Top}), blobs={blobs.Count}");

                var counters = CounterReader.Find(frame);
                Console.WriteLine($"counter reads: {counters.Count}");
                foreach (var c in counters)
                    Console.WriteLine($"  value={c.Value} digits={c.Digits} rect=({c.X0},{c.Y0})-({c.X1},{c.Y1})");

                var cds = CountdownReader.Find(frame);
                Console.WriteLine($"countdown reads: {cds.Count}");
                foreach (var c in cds)
                    Console.WriteLine($"  seconds={c.Seconds} ({c.Seconds / 60}:{c.Seconds % 60:D2}) rect=({c.X0},{c.Y0})-({c.X1},{c.Y1})");

                var chests = ChestDetector.Find(frame.Bits, frame.W, frame.H);
                Console.WriteLine($"chest matches: {chests.Count}");
                foreach (var m in chests)
                    Console.WriteLine($"  ({m.Cx:F0},{m.Cy:F0}) MAD={m.Mad:F1}");

                Console.WriteLine("SELFTEST OK");
                return 0;
            }
        }
    }
}
