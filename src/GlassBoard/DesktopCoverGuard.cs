using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassBoard;

// 显示桌面守护。
//
// 永久消失的根因是工具窗口被 SW_HIDE（见 GlassWindow 的窗口过程），不是“没置顶”。
// 这里只做两件补救，而且都只在状态变化时动 Z 序，避免 150ms 反复 SetWindowPos：
// 1. 桌面层真正盖住仍在屏幕上的 Dock/岛/组件时，临时 Topmost。外壳不会把桌面层插进置顶带。
// 2. 普通应用回到桌面层之上并稳定几拍后，取消临时置顶，并把窗口放到“最底下那个普通应用”的下面
//    （仍在桌面层上面）。不使用 HWND_BOTTOM，也不调用 ToggleDesktop——后者会把 Win+D 最小化的
//    所有窗口一起还原。
// 已经被藏起来的窗口在这里 Show / SW_SHOWNA 拉回来；游戏模式和灵动岛全屏策略通过 wantsVisible 排除。
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
	}

	private struct Sample
	{
		public IntPtr Hwnd;

		public ShowDesktopRules.ZWindow Z;
	}

	private static readonly List<Tracked> Windows = new List<Tracked>();

	private static readonly Dictionary<uint, bool> AmbientPid = new Dictionary<uint, bool>();

	private static DispatcherTimer timer;

	private static bool raised;

	private static bool ticking;

	private static ShowDesktopRules.Latch latch;

	public static bool ShowDesktopActive => raised;

	public static event Action ShowDesktopChanged;

	public static void Track(Window win, Action<bool> apply, Func<bool> wantsVisible)
	{
		if (win == null || apply == null)
		{
			return;
		}
		Tracked item = new Tracked
		{
			Win = win,
			Apply = apply,
			WantsVisible = wantsVisible
		};
		Windows.Add(item);
		win.Closed += (object s, EventArgs e) =>
		{
			Windows.RemoveAll((Tracked p) => ReferenceEquals(p.Win, win));
			if (Windows.Count == 0 && timer != null)
			{
				timer.Stop();
				timer = null;
				if (raised)
				{
					raised = false;
					latch = default(ShowDesktopRules.Latch);
					Notify();
				}
			}
		};
		if (raised)
		{
			try
			{
				apply(true);
			}
			catch (Exception ex)
			{
				Store.Log(ex);
			}
		}
		if (timer == null)
		{
			timer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(250.0)
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
			List<Sample> samples = Snapshot();
			List<ShowDesktopRules.ZWindow> order = new List<ShowDesktopRules.ZWindow>(samples.Count);
			for (int i = 0; i < samples.Count; i++)
			{
				order.Add(samples[i].Z);
			}
			bool covers = ShowDesktopRules.DesktopCoversLive(order);
			bool appsBack = ShowDesktopRules.LiveAppAboveDesktop(order);
			bool holding = ShowDesktopRules.ShellHolding(order);
			ShowDesktopRules.Latch next = ShowDesktopRules.Step(latch, covers, appsBack, holding);
			bool was = raised;
			IntPtr anchor = IntPtr.Zero;
			if (was && !next.On)
			{
				int index = ShowDesktopRules.AnchorIndex(order);
				if (index >= 0)
				{
					anchor = samples[index].Hwnd;
				}
			}
			latch = next;
			raised = next.On;
			if (was != raised)
			{
				for (int i = 0; i < Windows.Count; i++)
				{
					try
					{
						Windows[i].Apply(raised);
					}
					catch (Exception ex)
					{
						Store.Log(ex);
					}
				}
				if (!raised)
				{
					PlaceBelowAnchor(anchor);
				}
				Store.Trace("COVERGUARD raised=" + (raised ? "true" : "false"));
				Notify();
			}
			Reshow();
		}
		catch (Exception ex2)
		{
			Store.Log(ex2);
		}
		finally
		{
			ticking = false;
		}
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

	private static void PlaceBelowAnchor(IntPtr anchor)
	{
		if (anchor == IntPtr.Zero || !IsWindow(anchor))
		{
			Store.Trace("COVERGUARD relax without anchor");
			return;
		}
		const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
		for (int i = 0; i < Windows.Count; i++)
		{
			Window win = Windows[i].Win;
			try
			{
				if (!win.IsVisible || win.Topmost)
				{
					continue;
				}
				IntPtr hwnd = new WindowInteropHelper(win).Handle;
				if (hwnd == IntPtr.Zero || hwnd == anchor)
				{
					continue;
				}
				if ((GetWindowLong(hwnd, -20) & 8) != 0)
				{
					continue;
				}
				SetWindowPos(hwnd, anchor, 0, 0, 0, 0, flags);
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
				if (item.ReshowFails >= 3 || unchecked(now - item.LastReshow) < 1000)
				{
					continue;
				}
				item.LastReshow = now;
				item.ReshowFails++;
				Store.Trace("COVERGUARD reshow cls=" + win.GetType().Name + " win32=" + win32 + " wpf=" + wpf);
				if (!wpf)
				{
					win.Show();
				}
				hwnd = new WindowInteropHelper(win).Handle;
				if (hwnd != IntPtr.Zero && IsWindow(hwnd) && !IsWindowVisible(hwnd))
				{
					ShowWindow(hwnd, 8);
				}
				if (raised)
				{
					item.Apply(true);
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

	private static List<Sample> Snapshot()
	{
		uint self = GetCurrentProcessId();
		uint explorer = 0;
		IntPtr progman = FindWindow("Progman", null);
		if (progman != IntPtr.Zero)
		{
			GetWindowThreadProcessId(progman, out explorer);
		}
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
		List<Sample> list = new List<Sample>(128);
		HashSet<IntPtr> seen = new HashSet<IntPtr>();
		IntPtr cursor = GetTopWindow(IntPtr.Zero);
		while (cursor != IntPtr.Zero && list.Count < 2000 && seen.Add(cursor))
		{
			Sample sample = new Sample
			{
				Hwnd = cursor
			};
			bool visible = IsWindowVisible(cursor);
			sample.Z.Visible = visible;
			if (visible)
			{
				int ex = GetWindowLong(cursor, -20);
				uint pid;
				GetWindowThreadProcessId(cursor, out pid);
				string cls = ClassOf(cursor);
				bool desktopClass = ShowDesktopRules.IsDesktopClass(cls);
				sample.Z.Iconic = IsIconic(cursor);
				sample.Z.Topmost = (ex & 8) != 0;
				sample.Z.Tool = (ex & 0x80) != 0;
				sample.Z.Owned = GetWindow(cursor, 4) != IntPtr.Zero;
				sample.Z.Desktop = desktopClass && (cls == "Progman" || (explorer != 0 && pid == explorer));
				sample.Z.ShellChrome = ShowDesktopRules.IsShellChromeClass(cls);
				sample.Z.Tracked = tracked.Contains(cursor);
				sample.Z.Self = pid == self;
				GetWindowRect(cursor, out RECT rect);
				sample.Z.Width = rect.Right - rect.Left;
				sample.Z.Height = rect.Bottom - rect.Top;
				if (!sample.Z.Iconic && !sample.Z.Tool && !sample.Z.Desktop && !sample.Z.ShellChrome && !sample.Z.Tracked && pid != self && pid != 0)
				{
					sample.Z.Ambient = IsAmbient(pid);
				}
				if (visible && !sample.Z.Iconic && !sample.Z.Desktop && !sample.Z.Topmost)
				{
					int cloak;
					if (DwmGetWindowAttribute(cursor, 14, out cloak, 4) == 0 && cloak != 0)
					{
						sample.Z.Cloaked = true;
					}
				}
			}
			list.Add(sample);
			cursor = GetWindow(cursor, 2);
		}
		return list;
	}

	private static bool IsAmbient(uint pid)
	{
		bool known;
		if (AmbientPid.TryGetValue(pid, out known))
		{
			return known;
		}
		if (AmbientPid.Count > 256)
		{
			AmbientPid.Clear();
		}
		bool ambient = LooksLikeWallpaper(pid);
		AmbientPid[pid] = ambient;
		return ambient;
	}

	private static bool LooksLikeWallpaper(uint pid)
	{
		IntPtr handle = OpenProcess(0x1000, false, pid);
		if (handle == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			StringBuilder path = new StringBuilder(520);
			int size = path.Capacity;
			if (!QueryFullProcessImageName(handle, 0, path, ref size))
			{
				return false;
			}
			string file = Path.GetFileName(path.ToString());
			if (file.Length == 0)
			{
				return false;
			}
			return file.IndexOf("wallpaper", StringComparison.OrdinalIgnoreCase) >= 0 || file.IndexOf("upupoo", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private static string ClassOf(IntPtr hwnd)
	{
		StringBuilder name = new StringBuilder(96);
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
	private static extern IntPtr GetTopWindow(IntPtr hwnd);

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

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentProcessId();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exe, ref int size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
