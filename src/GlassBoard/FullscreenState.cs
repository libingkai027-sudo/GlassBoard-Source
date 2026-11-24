using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBoard;

public static class FullscreenState
{
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct MONITORINFO
	{
		public int Size;

		public RECT Monitor;

		public RECT Work;

		public uint Flags;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr window, out RECT rect);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, StringBuilder text, int length);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr window);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr window);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

	public static bool Active()
	{
		IntPtr foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero || !IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
		{
			return false;
		}
		int cloaked;
		if (DwmGetWindowAttribute(foregroundWindow, 14, out cloaked, 4) == 0 && cloaked != 0)
		{
			return false;
		}
		GetWindowThreadProcessId(foregroundWindow, out var id);
		if (id == (uint)Process.GetCurrentProcess().Id)
		{
			return false;
		}
		StringBuilder stringBuilder = new StringBuilder(256);
		GetClassName(foregroundWindow, stringBuilder, 256);
		if (new string[5] { "Progman", "WorkerW", "SHELLDLL_DefView", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }.Contains(stringBuilder.ToString()))
		{
			return false;
		}
		MONITORINFO info = new MONITORINFO
		{
			Size = Marshal.SizeOf(typeof(MONITORINFO))
		};
		if (!GetWindowRect(foregroundWindow, out var rect) || !GetMonitorInfo(MonitorFromWindow(foregroundWindow, 2u), ref info))
		{
			return false;
		}
		if (rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right)
		{
			return rect.Bottom >= info.Monitor.Bottom;
		}
		return false;
	}
}
