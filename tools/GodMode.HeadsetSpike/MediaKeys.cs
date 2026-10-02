using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GodMode.HeadsetSpike;

/// <summary>The keys a headset's buttons can arrive as: Windows' media and volume virtual keys.</summary>
public static class MediaKey
{
    public const int VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF;
    public const int NextTrack = 0xB0, PreviousTrack = 0xB1, Stop = 0xB2, PlayPause = 0xB3;
    public const int Play = 0xFA; // VK_PLAY, which some drivers send for AVRCP Play

    public static readonly IReadOnlyList<int> All = [PlayPause, NextTrack, PreviousTrack, Stop, Play, VolumeUp, VolumeDown, VolumeMute];

    public static bool IsMedia(int vk) => All.Contains(vk);

    public static string Name(int vk) => vk switch
    {
        PlayPause => "PlayPause",
        NextTrack => "NextTrack",
        PreviousTrack => "PreviousTrack",
        Stop => "Stop",
        Play => "Play",
        VolumeUp => "VolumeUp",
        VolumeDown => "VolumeDown",
        VolumeMute => "VolumeMute",
        _ => $"VK 0x{vk:X2}",
    };
}

/// <summary>
/// A low-level keyboard hook (WH_KEYBOARD_LL) for media keys: it sees them whichever window has focus, before any app,
/// and can swallow one (<see cref="Swallow"/>) so no other app gets it. Installed on the UI thread, whose message loop
/// runs it; Windows drops a hook that takes longer than its timeout, so the callback only logs and returns.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int LLKHF_INJECTED = 0x10;

    private readonly HookProc _proc; // kept alive: Windows holds only the pointer
    private readonly IntPtr _hook;
    private readonly Action<int, bool, bool, bool> _key;

    /// <param name="key">vk, down, injected, swallowed: for media keys only.</param>
    public KeyboardHook(Action<int, bool, bool, bool> key)
    {
        _key = key;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>The media key to swallow (both its down and up), or null for none.</summary>
    public int? Swallow { get; set; }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            if (MediaKey.IsMedia(vk))
            {
                var message = (int)wParam;
                var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                if (down || message is WM_KEYUP or WM_SYSKEYUP)
                {
                    var swallowed = Swallow == vk;
                    _key(vk, down, (info.flags & LLKHF_INJECTED) != 0, swallowed);
                    if (swallowed) return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

/// <summary>
/// Raw input from HID consumer-control devices (usage page 0x0C), with RIDEV_INPUTSINK so it arrives without focus: a
/// Bluetooth headset's AVRCP buttons come through Windows' AVRCP transport as such a device, if they come at all. It
/// says which device sent a press, which the keyboard hook cannot; it cannot swallow one.
/// </summary>
public static class RawConsumerInput
{
    public const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;

    public static void Register(IntPtr window)
    {
        RAWINPUTDEVICE[] devices = [new() { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = RIDEV_INPUTSINK, hwndTarget = window }];
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>A WM_INPUT's HID report: the device's name and the report's bytes; null for anything else.</summary>
    public static (string Device, byte[] Report)? Read(IntPtr lParam)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return null;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID) return null;
            var hid = buffer + (int)headerSize;
            var reportSize = Marshal.ReadInt32(hid);
            var count = Marshal.ReadInt32(hid, 4);
            var report = new byte[reportSize * count];
            Marshal.Copy(hid + 8, report, 0, report.Length);
            return (DeviceName(header.hDevice), report);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The consumer usage in a report (report id, then a 16-bit usage, as AVRCP's transport sends), by name.</summary>
    public static string Describe(byte[] report)
    {
        var hex = Convert.ToHexString(report);
        if (report.Length < 3) return hex;
        var usage = report[1] | (report[2] << 8);
        var name = usage switch
        {
            0 => "release",
            0xB0 => "Play",
            0xB1 => "Pause",
            0xB3 => "FastForward",
            0xB4 => "Rewind",
            0xB5 => "NextTrack",
            0xB6 => "PreviousTrack",
            0xB7 => "Stop",
            0xCD => "PlayPause",
            0xE2 => "Mute",
            0xE9 => "VolumeUp",
            0xEA => "VolumeDown",
            _ => $"usage 0x{usage:X}",
        };
        return $"{hex} ({name}?)";
    }

    private static string DeviceName(IntPtr device)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return "?";
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref chars) > 0
                ? Marshal.PtrToStringUni(buffer) ?? "?"
                : "?";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice, wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);
}
