using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassBoard;

// 显示桌面守护。
//
// 0.9.22.2 每 250ms 枚举全部顶层窗口，并用临时 Topmost 来回切换。
// 实测大约每秒 raised=true/false 一次：置顶时盖住正在看的窗口，放开时 HWND_NOTOPMOST
// 把 Dock 留在普通 Z 带中部，于是它又盖住更旧的普通窗口。这里不再置顶，也不再全量枚举。
// 每次只看前台窗口、Progman，以及已经登记的几个 HWND：
// - 前台是普通应用，且登记窗口在它上面或在桌面层中部 → 放到 Progman 正上方（所有普通窗口下面）
// - 前台是桌面层，且桌面层盖住登记窗口 → 用 HWND_TOP（非置顶）抬到桌面层之上
// 已经在目标位置就不再 SetWindowPos。启动台不在登记名单里，关闭它不会被这里重新显示。
public static class DesktopCoverGuard
{
	private sealed class Tracked
	{
		public Window Win;

		public Action<bool> Apply;

		public Func<bool> WantsVisible;

		public bool Seen;

		public int ReshowFails;

		public int LastReshow;

		public int LastPlace;
	}

	private static readonly List<Tracked> Windows = new List<Tracked>();

	private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);

	private const int ProbeSteps = 48;

	private const uint PlaceFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;

	private static DispatcherTimer timer;

	private static bool ticking;

	private static bool showDesktop;

	private static int quietTicks;

	public static bool ShowDesktopActive => showDesktop;

	public static event Action ShowDesktopChanged;

	public static void Track(Window win, Action<bool> apply, Func<bool> wantsVisible)
	{
		if (win == null || apply == null)
		{
			return;
		}
		Windows.Add(new Tracked
		{
			Win = win,
			Apply = apply,
			WantsVisible = wantsVisible
		});
		win.Closed += (object s, EventArgs e) =>
		{
			Windows.RemoveAll((Tracked p) => ReferenceEquals(p.Win, win));
			if (Windows.Count == 0 && timer != null)
			{
				timer.Stop();
				timer = null;
				if (showDesktop)
				{
					showDesktop = false;
					Notify();
				}
			}
		};
		if (timer == null)
		{
			timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1000.0)
			};
			timer.Tick += (object s, EventArgs e) => Tick();
			timer.Start();
		}
	}

	private static void Tick()
	{
		if (ticking || Windows.Count == 0)
		{
			return;
		}
		ticking = true;
		try
		{
			IntPtr foreground = GetForegroundWindow();
			bool shell = IsShellForeground(foreground);
			bool normal = !shell && IsNormalForeground(foreground);
			// 只有前台已经是桌面层时才顺着 Z 序找它。平时不扫窗口。
			bool covered = shell && AnyUnderDesktop();
			if (covered)
			{
				quietTicks = 0;
			}
			else if (quietTicks < 2)
			{
				quietTicks++;
			}
			bool session = covered || quietTicks < 2 && showDesktop;
			if (session != showDesktop)
			{
				showDesktop = session;
				Store.Trace("COVERGUARD showdesktop=" + (showDesktop ? "true" : "false"));
				Notify();
			}
			for (int i = 0; i < Windows.Count; i++)
			{
				PlaceIfNeeded(Windows[i], foreground, shell, normal);
			}
			Reshow();
		}
		catch (Exception ex)
		{
			Store.Log(ex);
		}
		finally
		{
			ticking = false;
		}
	}

	private static void PlaceIfNeeded(Tracked item, IntPtr foreground, bool shell, bool normal)
	{
		Window win = item.Win;
		if (win.Topmost)
		{
			return;
		}
		IntPtr hwnd;
		try
		{
			hwnd = new WindowInteropHelper(win).Handle;
		}
		catch (InvalidOperationException)
		{
			return;
		}
		if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
		{
			return;
		}
		bool bit = (GetWindowLong(hwnd, -20) & 8) != 0;
		if (shell)
		{
			if (!bit && !DesktopAbove(hwnd))
			{
				return;
			}
			int nowShell = Environment.TickCount;
			if (unchecked(nowShell - item.LastPlace) < 1500)
			{
				return;
			}
			item.LastPlace = nowShell;
			SetWindowPos(hwnd, bit ? HwndNotTopmost : IntPtr.Zero, 0, 0, 0, 0, PlaceFlags);
			Store.Trace("COVERGUARD place desktop cls=" + win.GetType().Name);
			return;
		}
		bool aboveForeground = normal && IsAbove(hwnd, foreground);
		bool aboveOlder = AboveNormalTowardDesktop(hwnd);
		if (!bit && !aboveForeground && !aboveOlder)
		{
			return;
		}
		int now = Environment.TickCount;
		if (unchecked(now - item.LastPlace) < 1500)
		{
			return;
		}
		item.LastPlace = now;
		IntPtr slot = LowestSlot();
		if (slot == IntPtr.Zero || slot == hwnd)
		{
			if (!bit || foreground == IntPtr.Zero)
			{
				return;
			}
			SetWindowPos(hwnd, foreground, 0, 0, 0, 0, PlaceFlags);
			Store.Trace("COVERGUARD clear-topmost cls=" + win.GetType().Name);
			return;
		}
		SetWindowPos(hwnd, slot, 0, 0, 0, 0, PlaceFlags);
		Store.Trace("COVERGUARD place under-apps cls=" + win.GetType().Name);
	}

	// Progman 正上方、跳过壳层和自己之后的那个窗口。把登记窗口插到它下面，就落在所有普通窗口之下、桌面层之上。
	private static IntPtr LowestSlot()
	{
		IntPtr prog = FindWindow("Progman", null);
		if (prog == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}
		IntPtr w = GetWindow(prog, 3u);
		for (int i = 0; i < 8 && w != IntPtr.Zero; i++)
		{
			if (!IsTracked(w) && !IsShellClass(w))
			{
				return w;
			}
			w = GetWindow(w, 3u);
		}
		return IntPtr.Zero;
	}

	// 从本窗口往桌面层方向最多看 80 个 HWND。看见普通窗口说明我们盖住了它。
	// 80 步以内没有，就不再往下扫，避免每秒走完整的顶层窗口表。
	private static bool AboveNormalTowardDesktop(IntPtr hwnd)
	{
		IntPtr prog = FindWindow("Progman", null);
		IntPtr w = hwnd;
		for (int i = 0; i < 80; i++)
		{
			w = GetWindow(w, 2u);
			if (w == IntPtr.Zero || w == prog)
			{
				return false;
			}
			if (IsShellClass(w))
			{
				continue;
			}
			if (IsNormalForeground(w))
			{
				return true;
			}
		}
		return false;
	}

	private static bool AnyUnderDesktop()
	{
		for (int i = 0; i < Windows.Count; i++)
		{
			try
			{
				IntPtr hwnd = new WindowInteropHelper(Windows[i].Win).Handle;
				if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && DesktopAbove(hwnd))
				{
					return true;
				}
			}
			catch (InvalidOperationException)
			{
			}
		}
		return false;
	}

	private static bool DesktopAbove(IntPtr hwnd)
	{
		IntPtr w = hwnd;
		for (int i = 0; i < ProbeSteps; i++)
		{
			w = GetWindow(w, 3u);
			if (w == IntPtr.Zero)
			{
				return false;
			}
			if (IsShellClass(w))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsAbove(IntPtr upper, IntPtr lower)
	{
		if (upper == IntPtr.Zero || lower == IntPtr.Zero || upper == lower)
		{
			return false;
		}
		IntPtr w = lower;
		for (int i = 0; i < ProbeSteps; i++)
		{
			w = GetWindow(w, 3u);
			if (w == IntPtr.Zero)
			{
				return false;
			}
			if (w == upper)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsTracked(IntPtr hwnd)
	{
		for (int i = 0; i < Windows.Count; i++)
		{
			try
			{
				if (new WindowInteropHelper(Windows[i].Win).Handle == hwnd)
				{
					return true;
				}
			}
			catch (InvalidOperationException)
			{
			}
		}
		return false;
	}

	private static bool IsShellForeground(IntPtr hwnd)
	{
		return hwnd != IntPtr.Zero && IsShellClass(hwnd);
	}

	private static bool IsNormalForeground(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsTracked(hwnd))
		{
			return false;
		}
		int ex = GetWindowLong(hwnd, -20);
		if ((ex & 8) != 0)
		{
			return false;
		}
		uint pid;
		GetWindowThreadProcessId(hwnd, out pid);
		if (pid == 0 || pid == GetCurrentProcessId())
		{
			return false;
		}
		if (!GetWindowRect(hwnd, out RECT rect))
		{
			return false;
		}
		int width = rect.Right - rect.Left;
		int height = rect.Bottom - rect.Top;
		bool tool = (ex & 0x80) != 0 || GetWindow(hwnd, 4u) != IntPtr.Zero;
		if (tool)
		{
			return width >= 800 && height >= 480;
		}
		return width >= ShowDesktopRules.MinLiveWidth && height >= ShowDesktopRules.MinLiveHeight;
	}

	private static bool IsShellClass(IntPtr hwnd)
	{
		return ShowDesktopRules.IsDesktopClass(ClassOf(hwnd));
	}

	private static void Notify()
	{
		Action handler = ShowDesktopChanged;
		if (handler == null)
		{
			return;
		}
		Delegate[] list = handler.GetInvocationList();
		for (int i = 0; i < list.Length; i++)
		{
			try
			{
				((Action)list[i])();
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
	}

	private static void Reshow()
	{
		int now = Environment.TickCount;
		for (int i = 0; i < Windows.Count; i++)
		{
			Tracked item = Windows[i];
			Window win = item.Win;
			try
			{
				if (win.Dispatcher.HasShutdownStarted)
				{
					continue;
				}
				IntPtr hwnd = new WindowInteropHelper(win).Handle;
				bool win32 = hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);
				bool wpf = win.IsVisible;
				if (win32 || wpf)
				{
					item.Seen = true;
					item.ReshowFails = 0;
				}
				bool wants = item.WantsVisible != null && item.WantsVisible();
				if (!ShowDesktopRules.ShouldReshow(item.Seen, wants, win32 || hwnd == IntPtr.Zero, wpf))
				{
					continue;
				}
				if (item.ReshowFails >= 3 || unchecked(now - item.LastReshow) < 5000)
				{
					continue;
				}
				item.LastReshow = now;
				item.ReshowFails++;
				Store.Trace("COVERGUARD reshow cls=" + win.GetType().Name);
				if (!wpf)
				{
					win.Show();
				}
				hwnd = new WindowInteropHelper(win).Handle;
				if (hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsWindowVisible(hwnd))
				{
					ShowWindow(hwnd, 8);
				}
			}
			catch (InvalidOperationException)
			{
				item.ReshowFails = 3;
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
	}

	private static string ClassOf(IntPtr hwnd)
	{
		StringBuilder name = new StringBuilder(64);
		if (GetClassName(hwnd, name, name.Capacity) <= 0)
		{
			return "";
		}
		return name.ToString();
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindow(string cls, string title);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hwnd);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
	private static extern int GetWindowLong(IntPtr hwnd, int index);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hwnd, int cmd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentProcessId();
}
