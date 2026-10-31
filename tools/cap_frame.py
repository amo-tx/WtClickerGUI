# -*- coding: utf-8 -*-
"""抓取 WuwaTappo 主窗一帧，保存 PNG 并输出最大可见块（桌宠）位置。"""
import ctypes, ctypes.wintypes as wt
from PIL import Image
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
PW_RENDERFULLCONTENT = 0x2

def enum_windows():
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, l):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, buf, 256)
        out.append((h, pid.value, buf.value))
        return True
    user32.EnumWindows(cb, 0)
    return out

def find_main():
    pids = set()
    import ctypes.wintypes as w
    arr = (w.DWORD * 4096)()
    cbf = ctypes.WINFUNCTYPE(w.BOOL, w.DWORD, w.LPDWORD)
    # EnumProcesses
    psapi = ctypes.windll.psapi
    cb = w.DWORD()
    psapi.EnumProcesses(arr, ctypes.sizeof(arr), ctypes.byref(cb))
    for i in range(cb.value // 4):
        if arr[i] == 0: continue
        name = ctypes.create_unicode_buffer(260)
        k32 = ctypes.windll.kernel32
        h = k32.OpenProcess(0x1000, False, arr[i])
        if h:
            k32.QueryFullProcessImageNameW(h, 0, name, ctypes.byref(w.DWORD(260)))
            k32.CloseHandle(h)
            if name.value.lower().endswith("wuwatappo.exe"):
                pids.add(arr[i])
    print("DBG pids:", pids)
    for h, pid, cls in enum_windows():
        if pid in pids and cls == "Tauri Window":
            t = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(h, t, 256)
            if t.value == "Wuwa Tappo":
                return h
    return None

def capture(hwnd):
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    wdc = user32.GetWindowDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(wdc)
    bmp = gdi32.CreateCompatibleBitmap(wdc, w, h)
    old = gdi32.SelectObject(mdc, bmp)
    ok = user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)
    class BMIH(ctypes.Structure):
        _fields_ = [("sz", wt.DWORD), ("w", ctypes.c_long), ("h", ctypes.c_long),
                    ("planes", wt.WORD), ("bits", wt.WORD), ("comp", wt.DWORD),
                    ("szimg", wt.DWORD), ("xppm", ctypes.c_long), ("yppm", ctypes.c_long),
                    ("used", wt.DWORD), ("imp", wt.DWORD)]
    bi = BMIH(ctypes.sizeof(BMIH), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.SelectObject(mdc, old); gdi32.DeleteObject(bmp); gdi32.DeleteDC(mdc)
    user32.ReleaseDC(hwnd, wdc)
    img = Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1)
    return img, (r.left, r.top)

if __name__ == "__main__":
    hwnd = find_main()
    if not hwnd:
        print("NO WINDOW"); sys.exit(1)
    img, origin = capture(hwnd)
    # 合成到黑底
    bg = Image.new("RGB", img.size, (0, 0, 0))
    bg.paste(img, mask=img.split()[3])
    bg.save(sys.argv[1] if len(sys.argv) > 1 else "frame.png")
    print("saved", img.size, "origin", origin)
