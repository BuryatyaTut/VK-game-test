using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WukongRunner.Core;

namespace WukongRunner.Cli.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowInput
{
    public static (int Width, int Height) Desktop => (GetSystemMetrics(0), GetSystemMetrics(1));
    public static void EnableDpiAwareness() => SetProcessDpiAwarenessContext(new IntPtr(-4));
    public static bool IsUsable(IntPtr window) => window != IntPtr.Zero && IsWindowVisible(window) &&
        GetClientRect(window, out var rect) && rect.Right > 0 && rect.Bottom > 0;

    public static async Task SendAsync(IntPtr window, int expectedPid, InputStep step, CancellationToken cancellationToken)
    {
        if (step.Kind == "wait") { await Task.Delay(step.DelayAfterMs, cancellationToken); return; }
        GetWindowThreadProcessId(window, out var pid);
        if (pid != expectedPid || !IsUsable(window)) throw new IOException("The owned benchmark window is no longer available.");
        ShowWindow(window, 9);
        SetForegroundWindow(window);
        await Task.Delay(250, cancellationToken);
        if (GetForegroundWindow() != window) throw new IOException("Cannot focus the benchmark window. Input was not sent.");

        Input down;
        Input up;
        if (step.Kind == "key")
        {
            var code = step.Key switch
            { "Enter" => 0x0D, "Space" => 0x20, "Escape" => 0x1B, "Up" => 0x26, "Down" => 0x28, "Left" => 0x25, "Right" => 0x27, "Tab" => 0x09, _ => throw new InvalidDataException("Unsupported key.") };
            down = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)code } } };
            up = down; up.Data.Keyboard.Flags = 0x0002;
        }
        else
        {
            if (!GetClientRect(window, out var rectangle)) throw new Win32Exception();
            var point = new Point { X = (int)Math.Round(step.X * (rectangle.Right - 1)), Y = (int)Math.Round(step.Y * (rectangle.Bottom - 1)) };
            if (!ClientToScreen(window, ref point)) throw new Win32Exception();
            // Avoid clicking outside the visible client area or through a covering window.
            if (WindowFromPoint(point) != window) throw new IOException("The target point is covered or outside the benchmark window.");
            if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception();
            down = new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } };
            up = down; up.Data.Mouse.Flags = 0x0004;
        }
        if (GetForegroundWindow() != window) throw new IOException("Focus changed before input; stopping the menu sequence.");
        var sent = SendInput(2, [down, up], Marshal.SizeOf<Input>());
        if (sent != 2)
        {
            SendInput(1, [up], Marshal.SizeOf<Input>());
            throw new IOException("Windows did not accept all input events; check window focus and matching privilege levels.");
        }
        await Task.Delay(step.DelayAfterMs, cancellationToken);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
