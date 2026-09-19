using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WoWSBattleAssistant.Services;

/// <summary>
/// 游戏窗口控制与按键输入。
/// 用途：截小地图前把游戏窗口置前、按 "+" 把小地图缩放到最大（截图更清晰），
/// 截图完成后按 "-" 恢复。
///
/// 【IDA 逆向结论（2026-09-19）】游戏键盘输入走 RawInput 主路径
/// （WndProc → WM_INPUT → os::EventConverter::pushRawEvent，见 sub_140313800），
/// PostMessage WM_KEYDOWN 只产生窗口消息、不产生 RawInput，游戏收不到 →
/// 之前"按 + 无效"的根因。必须用 SendInput（系统把它当作真实硬件输入，
/// 会产生 WM_INPUT），且用 KEYEVENTF_SCANCODE 扫描码模式（最接近真实硬件，
/// 不依赖键盘布局）。游戏窗口需在前台（AttachThreadInput + SetForegroundWindow）。
/// 缩放键双发（主键盘 +/- 与 小键盘 +/-），放大/恢复对称，多按无害（到顶/到底即停）。
/// </summary>
public static class GameInputHelper
{
    // ---- Win32 ----
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(IntPtr idAttach, IntPtr idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // 扫描码（硬件）：
    // 主键盘 '=' '+' 共用 scan 0x0D（+ 需 Shift）；主键盘 '-' '_' 共用 scan 0x0C
    // 小键盘 '+' scan 0x4E；小键盘 '-' scan 0x4A；左 Shift scan 0x2A
    private const byte SCAN_EQUALS = 0x0D;
    private const byte SCAN_MINUS = 0x0C;
    private const byte SCAN_NUMPAD_PLUS = 0x4E;
    private const byte SCAN_NUMPAD_MINUS = 0x4A;
    private const byte SCAN_LSHIFT = 0x2A;

    /// <summary>
    /// 查找游戏主窗口句柄。
    /// 注意：不能用 Process.MainWindowHandle —— 实测它会返回过期/无效句柄
    /// （游戏多窗口/全屏切换时缓存失效，IsWindow=0）。用 EnumWindows 枚举 +
    /// PID 匹配 + 可见窗口 + 类名 "App"（游戏主窗口类）定位。
    /// </summary>
    public static IntPtr FindGameWindow()
    {
        HashSet<int> gamePids = new();
        foreach (var name in new[] { "WorldOfWarships64", "WorldOfWarships" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                    gamePids.Add(p.Id);
            }
            catch { /* 进程可能已退出 */ }
        }
        if (gamePids.Count == 0) return IntPtr.Zero;

        IntPtr fallback = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!gamePids.Contains((int)pid)) return true;

            var cls = new System.Text.StringBuilder(128);
            GetClassNameW(hwnd, cls, cls.Capacity);
            var className = cls.ToString();

            // 游戏主窗口类名 "App"（实测），优先命中
            if (className.Equals("App", StringComparison.OrdinalIgnoreCase))
            {
                fallback = hwnd;
                return false; // 停止枚举
            }
            if (fallback == IntPtr.Zero) fallback = hwnd;
            return true;
        }, IntPtr.Zero);
        return fallback;
    }

    /// <summary>把游戏窗口激活到前台（若最小化先还原）。用 AttachThreadInput 绕过 Windows 前台锁。</summary>
    public static bool ActivateGameWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (IsIconic(hwnd))
                ShowWindow(hwnd, 9 /*SW_RESTORE*/);

            // AttachThreadInput 组合：让系统认为本线程与游戏线程"同输入状态"，允许抢前台
            var curThread = (IntPtr)GetCurrentThreadId();
            var gameThread = GetWindowThreadProcessId(hwnd, out _);
            bool attached = false;
            if (curThread != gameThread && gameThread != IntPtr.Zero)
            {
                attached = AttachThreadInput(curThread, gameThread, true);
            }
            try
            {
                return SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(curThread, gameThread, false);
            }
        }
        catch { return false; }
    }

    // ---- SendInput 扫描码注入（RawInput 能识别的唯一外部注入路径）----

    private static INPUT KeyInput(ushort scan, bool down)
    {
        return new INPUT
        {
            type = 1 /*INPUT_KEYBOARD*/,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | KEYEVENTF_SCANCODE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>发送一次按键序列（可选 Shift 修饰），扫描码模式。</summary>
    private static void SendScanKey(byte scan, bool withShift)
    {
        var list = new List<INPUT>();
        if (withShift) list.Add(KeyInput(SCAN_LSHIFT, down: true));
        list.Add(KeyInput(scan, down: true));
        list.Add(KeyInput(scan, down: false));
        if (withShift) list.Add(KeyInput(SCAN_LSHIFT, down: false));
        if (list.Count > 0)
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 发送一次"+"（先确保游戏前台）：主键盘 +（Shift+=）与小键盘 + 双发，
    /// 保证命中游戏硬编码的缩放键位。多按无害（小地图缩放到最大档即停）。
    /// </summary>
    public static void SendPlus(IntPtr hwnd)
    {
        ActivateGameWindow(hwnd);
        SendScanKey(SCAN_EQUALS, withShift: true);   // 主键盘 +
        Thread.Sleep(40);
        SendScanKey(SCAN_NUMPAD_PLUS, withShift: false); // 小键盘 +
    }

    /// <summary>发送一次"-"：主键盘 - 与小键盘 - 双发（与放大对称）。</summary>
    public static void SendMinus(IntPtr hwnd)
    {
        ActivateGameWindow(hwnd);
        SendScanKey(SCAN_MINUS, withShift: false);   // 主键盘 -
        Thread.Sleep(40);
        SendScanKey(SCAN_NUMPAD_MINUS, withShift: false); // 小键盘 -
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 把小地图缩放到最大（按 "+" times 次），等待渲染后截图，再按 "-" 恢复。
    /// 全程容错：任何一步失败都不影响游戏，调用方自行降级。
    /// </summary>
    public static bool ZoomMinimapMax(Action capture, int times = 6, int pressDelayMs = 100, int renderWaitMs = 450)
    {
        var hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero || !ActivateGameWindow(hwnd)) return false;

        try
        {
            // 放大
            for (int i = 0; i < times; i++)
            {
                SendPlus(hwnd);
                Thread.Sleep(pressDelayMs);
            }
            Thread.Sleep(renderWaitMs);
            capture();
        }
        catch
        {
            // 截图失败也继续走恢复流程
        }
        finally
        {
            // 恢复（多按几次确保回到原位，多按到底/到顶无害）
            for (int i = 0; i < times + 2; i++)
            {
                SendMinus(hwnd);
                Thread.Sleep(pressDelayMs);
            }
        }
        return true;
    }
}
