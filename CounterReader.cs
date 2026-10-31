using System;
using System.Collections.Generic;
using System.Linq;

namespace WtClickerGUI
{
    /// <summary>
    /// 桌宠「当前点击次数」胶囊读取 v4（2026-10-31 联机版，像素实测校准）：
    /// 本机桌宠底栏结构 = [计数数字][黑底米色✦圆钮][奶油底☰圆钮]，无用户名；
    /// 其他玩家的名牌只有用户名（可能含数字，如「13号」），没有 ✦ 圆钮 —— 结构级区别。
    /// 名牌与底栏会无缝相邻（联机帧实测），靠右侧 ✦ 锚点 + 数字大间隔规则隔离。
    /// 算法（全部基于列光剖面，不依赖连通域完整分离）：
    ///   ① 行光剖面找「底栏行带」（连续行光≥40 格，高 24~70px）；
    ///   ② 带内找 ✦ 特征：全光段(≥0.5×峰值, ≥40px) → 50px 内出现 ≥4 格纯黑隙(L≤0.15×峰值)
    ///      → 星列(0<L≤0.75×峰值) → 28px 内回到全光（☰ 圆钮）；
    ///   ③ 胶囊右缘=全光段末端，左缘向左扫（阈值 0.35×峰值，容 2 格缺口；可能扫进相邻名牌）；
    ///   ④ OCR 行带取竖直中心 25%~82%（避开垂进画面的桌宠耳朵/尾巴描边）；
    ///      从右往左收集字形段，遇到大间隔(>0.9×字高，即用户名与计数的布局间隔)停止；
    ///   ⑤ 粘连段按估计字宽(0.42×字高)均分拆分后逐字模板匹配（复用倒计时 16x24 模板）。
    /// </summary>
    internal static class CounterReader
    {
        public class CounterRead
        {
            public long Value;
            public int Digits;
            public int X0, Y0, X1, Y1;
        }

        public static List<CounterRead> Find(Frame f)
        {
            var result = new List<CounterRead>();
            if (f == null || f.Bits == null) return result;
            CountdownReader.EnsureLoaded();
            int w = f.W, h = f.H;
            var bits = f.Bits;

            // ---- 浅色掩码（奶油/白底：三通道最小值 > 180），步长 2
            var light = new HashSet<(int, int)>();
            var rowL = new Dictionary<int, int>();
            for (int y = 0; y + 1 < h; y += 2)
            {
                int row = y * w * 4;
                for (int x = 0; x + 1 < w; x += 2)
                {
                    int i = row + x * 4;
                    int mn = bits[i];
                    if (bits[i + 1] < mn) mn = bits[i + 1];
                    if (bits[i + 2] < mn) mn = bits[i + 2];
                    if (mn > 180)
                    {
                        light.Add((x, y));
                        rowL[y] = rowL.TryGetValue(y, out int c) ? c + 1 : 1;
                    }
                }
            }

            // ---- 行带：连续行光 ≥40 采样格、高度 24~70px 的横条（底栏行）
            var ys = new List<int>(rowL.Keys);
            ys.Sort();
            var bands = new List<(int y0, int y1)>();
            int bs = -1, bp = -1;
            foreach (int y in ys)
            {
                if (rowL[y] >= 40)
                {
                    if (bs < 0) bs = y;
                    bp = y;
                }
                else if (bs >= 0)
                {
                    if (bp - bs >= 24) bands.Add((bs, bp));
                    bs = -1;
                }
            }
            if (bs >= 0 && bp - bs >= 24) bands.Add((bs, bp));

            bool dbg = Environment.GetEnvironmentVariable("CTR_DEBUG") == "1";
            if (dbg) Console.Error.WriteLine($"[CTR] bands={string.Join(";", bands.Select(b => $"[{b.y0},{b.y1}]"))}");

            foreach (var (by0, by1) in bands)
            {
                if (by1 - by0 > 70) continue;
                int bh = by1 - by0;

                // 带内列光剖面（全 x 范围步进 2，包括纯黑列 L=0）
                int xLo = int.MaxValue, xHi = int.MinValue;
                foreach (var (x, y) in light)
                {
                    if (y >= by0 && y <= by1)
                    {
                        if (x < xLo) xLo = x;
                        if (x > xHi) xHi = x;
                    }
                }
                if (xLo >= xHi) continue;
                int nc = (xHi - xLo) / 2 + 1;
                var L = new int[nc];
                var xs = new int[nc];
                int maxL = 0;
                for (int i = 0; i < nc; i++)
                {
                    xs[i] = xLo + i * 2;
                    int cnt = 0;
                    for (int y = by0; y <= by1; y += 2)
                        if (light.Contains((xs[i], y))) cnt++;
                    L[i] = cnt;
                    if (cnt > maxL) maxL = cnt;
                }
                if (maxL < 20) continue;
                if (dbg) Console.Error.WriteLine($"[CTR] band[{by0},{by1}] maxL={maxL} x[{xLo},{xHi}]");

                double thFull = 0.5 * maxL, thGap = 0.15 * maxL, thStar = 0.75 * maxL, thLeft = 0.35 * maxL;

                // ---- 扫 ✦ 特征模式
                int i2 = 0;
                while (i2 < nc)
                {
                    if (L[i2] < thFull) { i2++; continue; }
                    int j = i2;
                    while (j < nc && L[j] >= thFull) j++;
                    if (xs[j - 1] - xs[i2] >= 40)
                    {
                        if (dbg) Console.Error.WriteLine($"[CTR] run[{xs[i2]},{xs[j - 1]}] len={xs[j - 1] - xs[i2]}");
                        // 过渡+黑隙+星+回光
                        int k = j, gapcnt = 0, star = -1;
                        while (k < nc && xs[k] - xs[j] < 50)
                        {
                            if (L[k] <= thGap) gapcnt++;
                            else if (L[k] > 0 && L[k] <= thStar && gapcnt >= 4) { star = k; break; }
                            k++;
                        }
                        if (star >= 0)
                        {
                            if (dbg) Console.Error.WriteLine($"[CTR]   star@{xs[star]} gapcnt={gapcnt}");
                            int m = star + 1; bool back = false;
                            while (m < nc && xs[m] - xs[star] < 28)
                            {
                                if (L[m] >= thFull) { back = true; break; }
                                m++;
                            }
                            if (back)
                            {
                                int capRight = xs[j - 1];
                                if (dbg) Console.Error.WriteLine($"[CTR]   ✦模式命中 capRight={capRight}");
                                // 左缘：低阈值扫描，容 2 格（4px）缺口
                                int p = j - 1, miss = 0, iLeft = j - 1;
                                while (p > 0)
                                {
                                    p--;
                                    if (L[p] >= thLeft) { iLeft = p; miss = 0; }
                                    else if (++miss > 2) break;
                                }
                                int capW = capRight - xs[iLeft];
                                if (dbg) Console.Error.WriteLine($"[CTR]   capLeft={xs[iLeft]} capW={capW}");
                                if (capW >= 60 && capW <= 500)
                                    TryOcr(f, light, xs[iLeft], capRight, by0, by1, result);
                            }
                        }
                    }
                    i2 = j;
                }
            }
            return result;
        }

        /// <summary>对 [capLeft,capRight]×[by0,by1] 的数字胶囊 OCR：右起收集、大间隔截断、粘连拆分、模板匹配</summary>
        static void TryOcr(Frame f, HashSet<(int, int)> light,
            int capLeft, int capRight, int by0, int by1, List<CounterRead> result)
        {
            int w = f.W;
            var bits = f.Bits;
            int bh = by1 - by0;
            int capW = capRight - capLeft;
            int tx0 = capLeft + (int)(capW * 0.03);
            int tx1 = capRight - (int)(capW * 0.02);
            // 竖直中心区：数字在栏内居中；桌宠耳朵/尾巴可能从顶上垂进来（黑描边会
            // 污染字形上缘），故上缘收 25%、下缘收 18%（数字实测 284~300 / 带 268~316）
            int ty0 = by0 + (int)(bh * 0.25), ty1 = by1 - (int)(bh * 0.18);
            int rh = ty1 - ty0, rw = tx1 - tx0;
            if (rh < 14 || rw < 24) return;

            var dark = new bool[rh, rw];
            var colSum = new int[rw];
            for (int y = 0; y < rh; y++)
            {
                int row = (ty0 + y) * w * 4;
                for (int x = 0; x < rw; x++)
                {
                    int i = row + (tx0 + x) * 4;
                    int lum = (bits[i] + bits[i + 1] + bits[i + 2]) / 3;
                    if (lum < 135) { dark[y, x] = true; colSum[x]++; }
                }
            }
            var segs = new List<(int s, int e)>();
            bool inSeg = false; int st = 0;
            for (int x = 0; x < rw; x++)
            {
                if (colSum[x] > 0 && !inSeg) { inSeg = true; st = x; }
                else if (colSum[x] == 0 && inSeg) { inSeg = false; segs.Add((st, x)); }
            }
            if (inSeg) segs.Add((st, rw));
            segs.RemoveAll(g => g.e - g.s < 3);
            if (segs.Count == 0) return;
            bool dbg = Environment.GetEnvironmentVariable("CTR_DEBUG") == "1";
            if (dbg) Console.Error.WriteLine($"[CTR]   OCR tx[{tx0},{tx1}] ty[{ty0},{ty1}] rh={rh} 段=" +
                string.Join(",", segs.Select(g => $"({g.s + tx0},{g.e + tx0})w{g.e - g.s}")));

            // 从右往左收集：遇到大间隔（用户名与计数的布局间隔）即停
            var picked = new List<(int s, int e)>();
            for (int gi = segs.Count - 1; gi >= 0; gi--)
            {
                if (picked.Count > 0)
                {
                    int gapPx = segs[gi + 1].s - segs[gi].e;
                    if (gapPx > 0.9 * rh) break;
                }
                picked.Insert(0, segs[gi]);
            }
            if (picked.Count == 0) return;
            if (dbg) Console.Error.WriteLine($"[CTR]   选取=" +
                string.Join(",", picked.Select(g => $"({g.s + tx0},{g.e + tx0})w{g.e - g.s}")));

            // 粘连拆分：宽段按估计字宽均分
            double digitW = 0.42 * rh;
            var glyphs = new List<(int s, int e)>();
            foreach (var (s, e) in picked)
            {
                int gw = e - s;
                int parts = Math.Max(1, (int)Math.Round(gw / digitW));
                if (parts <= 1) { glyphs.Add((s, e)); continue; }
                for (int q = 0; q < parts; q++)
                    glyphs.Add((s + (int)Math.Round(gw * q / (double)parts),
                                s + (int)Math.Round(gw * (q + 1) / (double)parts)));
            }
            if (glyphs.Count == 0 || glyphs.Count > 8) return;
            if (dbg) Console.Error.WriteLine($"[CTR]   拆分后=" +
                string.Join(",", glyphs.Select(g => $"({g.s + tx0},{g.e + tx0})w{g.e - g.s}")));

            long value = 0; int nDigits = 0;
            foreach (var (s, e) in glyphs)
            {
                int gy0 = int.MaxValue, gy1 = int.MinValue;
                for (int y = 0; y < rh; y++)
                    for (int x = s; x < e; x++)
                        if (dark[y, x]) { if (y < gy0) gy0 = y; if (y > gy1) gy1 = y; }
                if (gy0 > gy1) return;

                var gl = Normalize(dark, s, e, gy0, gy1);
                int GW = CountdownReader.GlyphW, GH = CountdownReader.GlyphH;
                int best = -1; double bMad = 9, sec = 9;
                for (int g = 0; g < 11; g++)
                {
                    double sum = 0;
                    for (int kk = 0; kk < GW * GH; kk++)
                        sum += gl[kk] != (CountdownReader.TplAt(g, kk) > 0.5f) ? 1 : 0;
                    double mad = sum / (GW * GH);
                    if (mad < bMad) { sec = bMad; bMad = mad; best = g; }
                    else if (mad < sec) sec = mad;
                }
                if (best < 0 || best == 10 || bMad > 0.30 || sec - bMad < 0.015)
                {
                    if (dbg) Console.Error.WriteLine($"[CTR]   模板匹配失败 best={best} bMad={bMad:F3} sec={sec:F3}");
                    return;
                }
                value = value * 10 + best;
                nDigits++;
            }
            if (nDigits == 0) return;
            result.Add(new CounterRead { Value = value, Digits = nDigits, X0 = capLeft, Y0 = by0, X1 = capRight, Y1 = by1 });
        }

        /// <summary>字形裁剪 → 最近邻缩放至 16x24</summary>
        static bool[] Normalize(bool[,] dark, int xs, int xe, int ys, int ye)
        {
            int GW = CountdownReader.GlyphW, GH = CountdownReader.GlyphH;
            int gw = xe - xs, gh = ye - ys + 1;
            var dst = new bool[GW * GH];
            for (int y = 0; y < GH; y++)
            {
                int sy = Math.Min(gh - 1, y * gh / GH);
                for (int x = 0; x < GW; x++)
                {
                    int sx = Math.Min(gw - 1, x * gw / GW);
                    dst[y * GW + x] = dark[ys + sy, xs + sx];
                }
            }
            return dst;
        }
    }
}
