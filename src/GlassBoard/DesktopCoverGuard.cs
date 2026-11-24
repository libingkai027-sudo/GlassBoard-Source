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

		public bool WasBelow;
	}

	private static readonly List<Tracked> Windows = new List<Tracked>();

	private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);

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
			bool foregroundNormal = IsNormalForeground(foreground);
			List<Ordered> order = BuildOrder(foreground, foregroundNormal);
			bool covered = AnyTrackedBelowWallpaper(order);
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
				PlaceIfNeeded(Windows[i], order);
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

	private struct Ordered
	{
		public IntPtr Hwnd;

		public ShowDesktopRules.ZWindow Z;
	}

	private static void PlaceIfNeeded(Tracked item, List<Ordered> order)
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
		if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
		{
			return;
		}
		int index = IndexOf(order, hwnd);
		List<ShowDesktopRules.ZWindow> view = ViewOf(order);
		ShowDesktopRules.PlaceKind action = index < 0 ? ShowDesktopRules.PlaceKind.None : ShowDesktopRules.ActionFor(view, index);
		bool below = action == ShowDesktopRules.PlaceKind.AboveWallpaper;
		if (below != item.WasBelow)
		{
			item.WasBelow = below;
			Store.Trace(TopoLine(win, hwnd, view, below ? "below" : "above", "none"));
		}
		if (!IsWindowVisible(hwnd) || action == ShowDesktopRules.PlaceKind.None)
		{
			return;
		}
		int now = Environment.TickCount;
		if (unchecked(now - item.LastPlace) < 1500)
		{
			return;
		}
		item.LastPlace = now;
		if (action == ShowDesktopRules.PlaceKind.BelowForeground)
		{
			int foreground = ShowDesktopRules.ForegroundIndex(view);
			if (foreground < 0)
			{
				return;
			}
			SetWindowPos(hwnd, order[foreground].Hwnd, 0, 0, 0, 0, PlaceFlags);
			Store.Trace(TopoLine(win, hwnd, view, "above-app", "below-foreground"));
			return;
		}
		int desk = ShowDesktopRules.HighestDesktopIndex(view);
		if (desk < 0)
		{
			return;
		}
		PlaceAboveLayer(hwnd, order[desk].Hwnd);
		Store.Trace(TopoLine(win, hwnd, view, "below", "above-wallpaper"));
	}

	// 插到最高可见壁纸层的正上方。本机那一层是带 SHELLDLL_DefView 的 WorkerW，它还在 Wallpaper Engine 的 WorkerW 和 Progman 前面。
	private static void PlaceAboveLayer(IntPtr hwnd, IntPtr layer)
	{
		IntPtr prev = GetWindow(layer, 3u);
		for (int i = 0; i < 12 && prev != IntPtr.Zero; i++)
		{
			if (prev == hwnd)
			{
				return;
			}
			if (!IsTracked(prev) && !IsDesktopLayer(prev) && (GetWindowLong(prev, -20) & 8) == 0)
			{
				break;
			}
			prev = GetWindow(prev, 3u);
		}
		bool bit = (GetWindowLong(hwnd, -20) & 8) != 0;
		IntPtr after = prev == IntPtr.Zero || prev == hwnd ? (bit ? HwndNotTopmost : IntPtr.Zero) : prev;
		SetWindowPos(hwnd, after, 0, 0, 0, 0, PlaceFlags);
	}

	private static string TopoLine(Window win, IntPtr hwnd, List<ShowDesktopRules.ZWindow> view, string relation, string action)
	{
		int cloak = 0;
		DwmGetWindowAttribute(hwnd, 14, out cloak, 4);
		bool iconic = IsIconic(hwnd);
		bool visible = IsWindowVisible(hwnd);
		bool topmost = (GetWindowLong(hwnd, -20) & 8) != 0;
		return "COVERGUARD topo cls=" + win.GetType().Name + " vis=" + (visible ? 1 : 0) + " iconic=" + (iconic ? 1 : 0) + " cloak=" + cloak + " topmost=" + (topmost ? 1 : 0) + " desk=" + ShowDesktopRules.HighestDesktopKind(view) + " relation=" + relation + " action=" + action;
	}

	private static List<Ordered> BuildOrder(IntPtr foreground, bool foregroundNormal)
	{
		HashSet<IntPtr> tracked = new HashSet<IntPtr>();
		for (int i = 0; i < Windows.Count; i++)
		{
			try
			{
				IntPtr hwnd = new WindowInteropHelper(Windows[i].Win).Handle;
				if (hwnd != IntPtr.Zero)
				{
					tracked.Add(hwnd);
				}
			}
			catch (InvalidOperationException)
			{
			}
		}
		List<Ordered> list = new List<Ordered>();
		HashSet<IntPtr> seen = new HashSet<IntPtr>();
		IntPtr cursor = GetTopWindow(IntPtr.Zero);
		while (cursor != IntPtr.Zero && seen.Add(cursor))
		{
			string cls = ClassOf(cursor);
			ShowDesktopRules.ZWindow z = new ShowDesktopRules.ZWindow
			{
				Visible = IsWindowVisible(cursor)
			};
			if (cls == "Progman")
			{
				z.Kind = ShowDesktopRules.DesktopKind.Progman;
				z.Desktop = true;
			}
			else if (cls == "WorkerW" && z.Visible)
			{
				if (FindWindowEx(cursor, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
				{
					z.Kind = ShowDesktopRules.DesktopKind.IconHost;
					z.Desktop = true;
				}
				else if (FindWindowEx(cursor, IntPtr.Zero, "WPEDesktopDX11Window", null) != IntPtr.Zero)
				{
					z.Kind = ShowDesktopRules.DesktopKind.WallpaperHost;
					z.Desktop = true;
				}
			}
			if (tracked.Contains(cursor))
			{
				z.Tracked = true;
			}
			if (cursor == foreground && foregroundNormal)
			{
				z.Foreground = true;
				GetWindowRect(cursor, out RECT rect);
				z.Width = rect.Right - rect.Left;
				z.Height = rect.Bottom - rect.Top;
				z.Visible = true;
			}
			if (z.Desktop || z.Tracked || z.Foreground)
			{
				list.Add(new Ordered
				{
					Hwnd = cursor,
					Z = z
				});
			}
			cursor = GetWindow(cursor, 2u);
		}
		return list;
	}

	private static bool AnyTrackedBelowWallpaper(List<Ordered> order)
	{
		List<ShowDesktopRules.ZWindow> view = ViewOf(order);
		for (int i = 0; i < order.Count; i++)
		{
			if (order[i].Z.Tracked && ShowDesktopRules.ActionFor(view, i) == ShowDesktopRules.PlaceKind.AboveWallpaper)
			{
				return true;
			}
		}
		return false;
	}

	private static int IndexOf(List<Ordered> order, IntPtr hwnd)
	{
		for (int i = 0; i < order.Count; i++)
		{
			if (order[i].Hwnd == hwnd)
			{
				return i;
			}
		}
		return -1;
	}

	private static List<ShowDesktopRules.ZWindow> ViewOf(List<Ordered> order)
	{
		List<ShowDesktopRules.ZWindow> view = new List<ShowDesktopRules.ZWindow>(order.Count);
		for (int i = 0; i < order.Count; i++)
		{
			view.Add(order[i].Z);
		}
		return view;
	}

	private static bool IsDesktopLayer(IntPtr hwnd)
	{
		string cls = ClassOf(hwnd);
		if (cls == "Progman")
		{
			return true;
		}
		if (cls != "WorkerW")
		{
			return false;
		}
		return FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero || FindWindowEx(hwnd, IntPtr.Zero, "WPEDesktopDX11Window", null) != IntPtr.Zero;
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
	private static extern IntPtr GetTopWindow(IntPtr hwnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string title);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

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
