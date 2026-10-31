using System;

namespace WtClickerGUI
{
    // 测试桩：与 Scanner.Frame 同构（CounterReader 只用到 Bits/W/H）
    internal class Frame
    {
        public byte[] Bits;
        public int W, H;
        public int Left, Top;
    }
}
