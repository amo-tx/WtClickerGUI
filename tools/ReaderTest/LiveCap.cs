using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WtClickerGUI
{
    /// <summary>
    /// 实机抓帧（与引擎 Scanner.CaptureFrame 完全一致的路径：PrintWindow + GetDIBits，
    /// 不做 alpha 合成 —— 用于复现「离线帧能读、引擎读不到」的差异）。
    /// </summary>
    internal static class LiveCap
    {
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BIH bi, uint usage);
        delegate bool EnumProc(IntPtr h, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct BIH
        {
            public uint biSize; public int biWidth, biHeight; public short biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPels, biYPels; public uint biClrUsed, biClrImportant;
        }
        const uint PW_RENDERFULLCONTENT = 0x2;

        static string ClassName(IntPtr h) { var b = new StringBuilder(256); GetClassName(h, b, 256); return b.ToString(); }
        static string Title(IntPtr h) { var b = new StringBuilder(256); GetWindowText(h, b, 256); return b.ToString(); }

        public static Frame Capture()
        {
            var pids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("WuwaTappo")) pids.Add((uint)p.Id);
            if (pids.Count == 0) { Console.WriteLine("WuwaTappo 未运行"); return null; }
            IntPtr hwnd = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && ClassName(h) == "Tauri Window" && Title(h) == "Wuwa Tappo")
                { hwnd = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Console.WriteLine("未找到主窗"); return null; }

            GetWindowRect(hwnd, out RECT r);
            int w = r.Right - r.Left, h2 = r.Bottom - r.Top;
            IntPtr wdc = GetWindowDC(hwnd);
            IntPtr mdc = CreateCompatibleDC(wdc);
            IntPtr bmp = CreateCompatibleBitmap(wdc, w, h2);
            IntPtr old = SelectObject(mdc, bmp);
            try
            {
                if (!PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)) { Console.WriteLine("PrintWindow 失败"); return null; }
                var bi = new BIH { biSize = (uint)Marshal.SizeOf<BIH>(), biWidth = w, biHeight = -h2, biPlanes = 1, biBitCount = 32 };
                var bits = new byte[w * h2 * 4];
                GetDIBits(mdc, bmp, 0, (uint)h2, bits, ref bi, 0);
                return new Frame { Bits = bits, W = w, H = h2, Left = r.Left, Top = r.Top };
            }
            finally
            {
                SelectObject(mdc, old); DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(hwnd, wdc);
            }
        }

        public static void Run(string savePath)
        {
            var f = Capture();
            if (f == null) return;
            Console.WriteLine($"实机帧 {f.W}x{f.H} origin({f.Left},{f.Top})");
            var reads = CounterReader.Find(f);
            Console.WriteLine($"CounterReader: {reads.Count} 个读数");
            foreach (var rd in reads)
                Console.WriteLine($"  值={rd.Value} 位数={rd.Digits} bbox=({rd.X0},{rd.Y0})-({rd.X1},{rd.Y1})");
            if (reads.Count == 0) Console.WriteLine("  （无读数 —— 复现引擎失败！）");
            if (savePath != null)
            {
                using var fs = new FileStream(savePath, FileMode.Create);
                fs.Write(BitConverter.GetBytes(f.W), 0, 4);
                fs.Write(BitConverter.GetBytes(f.H), 0, 4);
                fs.Write(f.Bits, 0, f.Bits.Length);
                Console.WriteLine("帧已保存: " + savePath);
            }
        }
    }
}
