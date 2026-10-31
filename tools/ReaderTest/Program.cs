using System;
using System.IO;
using System.Linq;

namespace ReaderTest
{
    class Program
    {
        static void Main(string[] args)
        {
            // --live [保存路径]：走引擎同款 PrintWindow 抓帧路径实测（不做 alpha 合成）
            if (args.Length > 0 && args[0] == "--live")
            {
                WtClickerGUI.LiveCap.Run(args.Length > 1 ? args[1] : null);
                return;
            }

            foreach (var path in args)
            {
                byte[] all = File.ReadAllBytes(path);
                // 文件头 8 字节: w(int32) h(int32)，其余 BGRA
                int w = BitConverter.ToInt32(all, 0);
                int h = BitConverter.ToInt32(all, 4);
                var bits = new byte[all.Length - 8];
                Array.Copy(all, 8, bits, 0, bits.Length);
                var f = new WtClickerGUI.Frame { Bits = bits, W = w, H = h, Left = 0, Top = 0 };
                var reads = WtClickerGUI.CounterReader.Find(f);
                Console.WriteLine($"{Path.GetFileName(path)} ({w}x{h}): {reads.Count} 个读数");
                foreach (var r in reads)
                    Console.WriteLine($"  值={r.Value} 位数={r.Digits} bbox=({r.X0},{r.Y0})-({r.X1},{r.Y1})");
                if (reads.Count == 0) Console.WriteLine("  （无读数）");

                // ---- 宝箱检测
                try
                {
                    var chests = WtClickerGUI.ChestDetector.Find(bits, w, h);
                    Console.WriteLine($"  宝箱: {chests.Count} 个");
                    foreach (var c in chests)
                        Console.WriteLine($"    中心=({c.Item1:F0},{c.Item2:F0}) MAD={c.Item3:F1}");
                    ChestDiag.Run(bits, w, h);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  宝箱检测异常: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}
