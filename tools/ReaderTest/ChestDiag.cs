using System;
using System.Collections.Generic;
using System.IO;

namespace ReaderTest
{
    // 宝箱检测诊断：复刻 ChestDetector 掩码聚类，打印每簇的最佳 MAD（不看阈值）
    static class ChestDiag
    {
        public static void Run(byte[] bits, int w, int h)
        {
            WtClickerGUI.ChestDetector.EnsureLoaded();
            var cells = new HashSet<(int, int)>();
            for (int y = 0; y + 3 < h; y += 4)
            {
                int row = y * w * 4;
                for (int x = 0; x + 3 < w; x += 4)
                {
                    int i = row + x * 4;
                    int b = bits[i], g = bits[i + 1], r = bits[i + 2];
                    if (r > 190 && g > 130 && g < 235 && b > 60 && b < 185 && r > g + 15 && g > b + 30)
                        cells.Add((x, y));
                }
            }
            Console.WriteLine($"orange cells: {cells.Count}");
            var seen = new HashSet<(int, int)>();
            foreach (var p in cells)
            {
                if (seen.Contains(p)) continue;
                var comp = new List<(int, int)>();
                var stack = new Stack<(int, int)>();
                stack.Push(p); seen.Add(p);
                int x0 = p.Item1, y0 = p.Item2, x1 = p.Item1, y1 = p.Item2;
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); comp.Add(c);
                    if (c.Item1 < x0) x0 = c.Item1;
                    if (c.Item1 > x1) x1 = c.Item1;
                    if (c.Item2 < y0) y0 = c.Item2;
                    if (c.Item2 > y1) y1 = c.Item2;
                    for (int dx = -4; dx <= 4; dx += 4)
                        for (int dy = -4; dy <= 4; dy += 4)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (n.Item1 != c.Item1 && n.Item2 != c.Item2) continue;
                            if (cells.Contains(n) && !seen.Contains(n)) { seen.Add(n); stack.Push(n); }
                        }
                }
                if (comp.Count < 40) continue;
                int sx0 = Math.Max(0, x0 - 34), sy0 = Math.Max(0, y0 - 34);
                int sx1 = Math.Min(w - 100, x1 + 34), sy1 = Math.Min(h - 78, y1 + 34);
                double best = double.MaxValue; int bx = -1, by = -1;
                for (int yy = sy0; yy <= sy1; yy += 2)
                    for (int xx = sx0; xx <= sx1; xx += 2)
                    {
                        double mad = WtClickerGUI.ChestDetector.MadAtPublic(bits, w, xx, yy);
                        if (mad < best) { best = mad; bx = xx; by = yy; }
                    }
                Console.WriteLine($"  簇 n={comp.Count} bbox=({x0},{y0})-({x1},{y1}) → 最佳MAD={best:F1} @({bx},{by}) (阈值10)");
            }
        }
    }
}
