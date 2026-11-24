using System;
using System.Collections.Generic;

namespace GlassBoard;

// Win+D /「显示桌面」的纯判定。不调用 user32，方便在没有桌面会话时回归。
//
// 根因（Windows 10 22H2 实测状态，不是猜测性的“第一个窗口是不是 Progman”）：
// GlassBoard 窗口是 WS_EX_TOOLWINDOW（不进任务栏）。显示桌面对普通窗口发 SC_MINIMIZE，
// 对工具窗口走 ShowWindow(SW_HIDE)。SW_HIDE 会先发 WM_SHOWWINDOW，再 SetWindowPos(SWP_HIDEWINDOW)。
// 只吞掉 WM_SHOWWINDOW 取消不了隐藏，窗口会变成 IsWindowVisible=false、非最小化、非 Cloak，
// 而且外壳不会在下一次 Win+D 把这种窗口恢复回来。所以“消失”是 WS_VISIBLE 被清掉，不是 Z 序。
//
// Z 序只负责“桌面层被抬到普通窗口之上时，临时让 Dock/岛/组件还能看见”。判定必须：
// - 跳过 WS_EX_TOOLWINDOW / 被拥有的弹出窗口（Edge 的小工具窗会挡在 WorkerW 前面）
// - 走完整 Z 序，不能只向上数 64 个 HWND（300+ 顶层窗口时桌面层在栈底）
// - 只把 explorer 的 Progman / WorkerW / DefView 当桌面层（Wallpaper Engine 另有 WorkerW）
// - 已最小化的窗口平时就排在 Progman 下面，不能据此认为“正在显示桌面”
public static class ShowDesktopRules
{
	public const uint SwpHideWindow = 0x0080;

	public const int ReleaseSamples = 3;

	public const int MinLiveWidth = 160;

	public const int MinLiveHeight = 120;

	public struct ZWindow
	{
		public bool Visible;

		public bool Iconic;

		public bool Topmost;

		public bool Tool;

		public bool Owned;

		public bool Cloaked;

		public bool Desktop;

		public bool Tracked;

		public bool Self;

		public bool Ambient;

		public bool ShellChrome;

		public int Width;

		public int Height;
	}

	public struct Latch
	{
		public bool On;

		public int Calm;
	}

	public static bool IsDesktopClass(string cls)
	{
		return cls == "Progman" || cls == "WorkerW" || cls == "SHELLDLL_DefView";
	}

	public static bool IsShellChromeClass(string cls)
	{
		return IsDesktopClass(cls) || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "NotifyIconOverflowWindow";
	}

	// 外壳 SW_HIDE / 清 WS_VISIBLE：只有本程序主动隐藏或窗口正在关闭才放行。
	public static uint AllowHideFlags(uint flags, bool consent, bool closing)
	{
		if ((flags & SwpHideWindow) == 0 || consent || closing)
		{
			return flags;
		}
		return flags & ~SwpHideWindow;
	}

	public static bool ShouldReshow(bool seenVisible, bool wantsVisible, bool win32Visible, bool wpfVisible)
	{
		return seenVisible && wantsVisible && (!win32Visible || !wpfVisible);
	}

	// 桌面层是否压在“还在屏幕上的内容”之上。最小化窗口不算：它们平时就在 Progman 下面。
	public static bool DesktopCoversLive(IReadOnlyList<ZWindow> topToBottom)
	{
		bool seenDesktop = false;
		for (int i = 0; i < topToBottom.Count; i++)
		{
			ZWindow w = topToBottom[i];
			if (!w.Visible || w.Cloaked || w.Topmost)
			{
				continue;
			}
			if (w.Desktop)
			{
				seenDesktop = true;
				continue;
			}
			if (!seenDesktop || w.Iconic || w.Ambient)
			{
				continue;
			}
			if (w.Tracked)
			{
				return true;
			}
			if (w.Tool || w.Owned || w.ShellChrome || w.Self)
			{
				continue;
			}
			if (w.Width < MinLiveWidth || w.Height < MinLiveHeight)
			{
				continue;
			}
			return true;
		}
		return false;
	}

	// 有一个真正的应用窗口已经回到桌面层之上（显示桌面结束）。
	public static bool LiveAppAboveDesktop(IReadOnlyList<ZWindow> topToBottom)
	{
		bool app = false;
		for (int i = 0; i < topToBottom.Count; i++)
		{
			ZWindow w = topToBottom[i];
			if (!w.Visible || w.Cloaked || w.Topmost)
			{
				continue;
			}
			if (w.Desktop)
			{
				return app;
			}
			if (w.Iconic || w.Tool || w.Owned || w.Tracked || w.Self || w.Ambient || w.ShellChrome)
			{
				continue;
			}
			if (w.Width < MinLiveWidth || w.Height < MinLiveHeight)
			{
				continue;
			}
			app = true;
		}
		return false;
	}

	// 显示桌面仍把普通窗口最小化挂在抬高的桌面层下面，且最上面的非工具窗口就是桌面层。
	// 任务栏和工具窗（含 Edge 小窗）跳过，否则会把“桌面已经在最上面”误判成没有。
	public static bool ShellHolding(IReadOnlyList<ZWindow> topToBottom)
	{
		bool iconic = false;
		bool saw = false;
		bool firstIsDesktop = false;
		for (int i = 0; i < topToBottom.Count; i++)
		{
			ZWindow w = topToBottom[i];
			if (!w.Visible || w.Cloaked)
			{
				continue;
			}
			if (w.Iconic && !w.Desktop && !w.ShellChrome)
			{
				iconic = true;
			}
			if (w.Topmost || w.Iconic || w.Tool || w.Owned || w.Ambient || w.Tracked || w.Self)
			{
				continue;
			}
			if (w.ShellChrome && !w.Desktop)
			{
				continue;
			}
			if (!saw)
			{
				saw = true;
				firstIsDesktop = w.Desktop;
			}
		}
		return saw && firstIsDesktop && iconic;
	}

	// 复位时插到“桌面层正上方那个普通应用”的下面，这样前台窗口盖住 Dock，Dock 仍盖住桌面。
	// 返回 -1 表示上面没有普通应用（桌面层仍在非置顶带最上方）——这时不能下沉。
	public static int AnchorIndex(IReadOnlyList<ZWindow> topToBottom)
	{
		int desktop = -1;
		for (int i = 0; i < topToBottom.Count; i++)
		{
			ZWindow w = topToBottom[i];
			if (w.Visible && !w.Cloaked && w.Desktop)
			{
				desktop = i;
				break;
			}
		}
		if (desktop <= 0)
		{
			return -1;
		}
		for (int i = desktop - 1; i >= 0; i--)
		{
			ZWindow w = topToBottom[i];
			if (!w.Visible || w.Cloaked || w.Topmost || w.Desktop || w.ShellChrome)
			{
				continue;
			}
			if (w.Tool || w.Owned || w.Ambient || w.Tracked || w.Self || w.Iconic)
			{
				continue;
			}
			if (w.Width < MinLiveWidth || w.Height < MinLiveHeight)
			{
				continue;
			}
			return i;
		}
		return -1;
	}

	public static Latch Step(Latch prev, bool covers, bool appsBack, bool shellHolding)
	{
		Latch next = prev;
		if (!next.On)
		{
			next.Calm = 0;
			if (covers)
			{
				next.On = true;
			}
			return next;
		}
		if (covers || (!appsBack && shellHolding))
		{
			next.Calm = 0;
			return next;
		}
		next.Calm++;
		if (next.Calm >= ReleaseSamples)
		{
			next.On = false;
			next.Calm = 0;
		}
		return next;
	}

	// 灵动岛全屏隐藏。显示桌面期间的前台抖动不是全屏游戏；手动游戏模式仍然隐藏。
	public static bool ShouldHideIsland(bool showDesktop, bool manualGame, bool autoGame, bool fullscreen, bool maximized, bool showOnFullscreen, bool expanded, bool toast, bool alreadyHidden)
	{
		if (alreadyHidden || toast)
		{
			return false;
		}
		if (showDesktop && !manualGame)
		{
			return false;
		}
		bool full = fullscreen && !maximized;
		bool game = manualGame || (autoGame && fullscreen);
		if (!(full || game))
		{
			return false;
		}
		if ((game || !showOnFullscreen) && (game || !expanded))
		{
			return true;
		}
		return false;
	}

	public static bool ShouldRestoreIsland(bool showDesktop, bool manualGame, bool hiddenByPolicy, bool enabled, bool fullscreen, bool maximized, bool autoGame)
	{
		if (!hiddenByPolicy || !enabled || manualGame)
		{
			return false;
		}
		if (showDesktop)
		{
			return true;
		}
		bool full = fullscreen && !maximized;
		bool game = autoGame && fullscreen;
		return !(full || game);
	}

	public static void RunChecks()
	{
		Expect(AllowHideFlags(SwpHideWindow | 0x13, consent: false, closing: false) == 0x13u, "strip SWP_HIDEWINDOW");
		Expect(AllowHideFlags(SwpHideWindow, consent: true, closing: false) == SwpHideWindow, "consent hide stays");
		Expect(AllowHideFlags(SwpHideWindow, consent: false, closing: true) == SwpHideWindow, "closing hide stays");
		Expect(AllowHideFlags(0x13, consent: false, closing: false) == 0x13u, "no hide flag untouched");
		Expect(!ShouldReshow(seenVisible: false, wantsVisible: true, win32Visible: false, wpfVisible: false), "do not show before first show");
		Expect(!ShouldReshow(seenVisible: true, wantsVisible: false, win32Visible: false, wpfVisible: false), "game mode stays hidden");
		Expect(ShouldReshow(seenVisible: true, wantsVisible: true, win32Visible: false, wpfVisible: true), "win32 hide with wpf still visible");
		Expect(ShouldReshow(seenVisible: true, wantsVisible: true, win32Visible: false, wpfVisible: false), "fully hidden chrome");
		Expect(!ShouldReshow(seenVisible: true, wantsVisible: true, win32Visible: true, wpfVisible: true), "already visible");
		Expect(!GameModeRules.Suppressed(manual: false, automatic: true, fullscreen: true, desktop: true), "auto game yields on show desktop");
		Expect(GameModeRules.Suppressed(manual: false, automatic: true, fullscreen: true, desktop: false), "auto game still hides on a real fullscreen");
		Expect(GameModeRules.Suppressed(manual: true, automatic: true, fullscreen: false, desktop: true), "manual game stays hidden on the desktop");
		Expect(!ShouldHideIsland(showDesktop: true, manualGame: false, autoGame: true, fullscreen: true, maximized: false, showOnFullscreen: false, expanded: false, toast: false, alreadyHidden: false), "show desktop is not island fullscreen");
		Expect(ShouldHideIsland(showDesktop: false, manualGame: false, autoGame: false, fullscreen: true, maximized: false, showOnFullscreen: false, expanded: false, toast: false, alreadyHidden: false), "borderless fullscreen still hides");
		Expect(!ShouldHideIsland(showDesktop: false, manualGame: false, autoGame: false, fullscreen: true, maximized: true, showOnFullscreen: false, expanded: false, toast: false, alreadyHidden: false), "maximized is not fullscreen");
		Expect(ShouldHideIsland(showDesktop: false, manualGame: false, autoGame: true, fullscreen: true, maximized: true, showOnFullscreen: true, expanded: false, toast: false, alreadyHidden: false), "auto game still hides a covering window");
		Expect(ShouldHideIsland(showDesktop: true, manualGame: true, autoGame: false, fullscreen: false, maximized: false, showOnFullscreen: true, expanded: false, toast: false, alreadyHidden: false), "manual game hides during show desktop");
		Expect(!ShouldHideIsland(showDesktop: false, manualGame: false, autoGame: false, fullscreen: true, maximized: false, showOnFullscreen: false, expanded: false, toast: true, alreadyHidden: false), "toast is not force-hidden");
		Expect(ShouldRestoreIsland(showDesktop: true, manualGame: false, hiddenByPolicy: true, enabled: true, fullscreen: true, maximized: false, autoGame: true), "restore island when show desktop caused the hide");
		Expect(!ShouldRestoreIsland(showDesktop: true, manualGame: true, hiddenByPolicy: true, enabled: true, fullscreen: false, maximized: false, autoGame: false), "manual game does not restore");
		Expect(!ShouldRestoreIsland(showDesktop: false, manualGame: false, hiddenByPolicy: true, enabled: true, fullscreen: true, maximized: false, autoGame: false), "real fullscreen stays hidden");
		Expect(ShouldRestoreIsland(showDesktop: false, manualGame: false, hiddenByPolicy: true, enabled: true, fullscreen: false, maximized: false, autoGame: false), "restore after fullscreen ends");
		ZWindow edge = Live(tool: true, width: 344, height: 206);
		ZWindow notepad = Live(width: 900, height: 700);
		ZWindow worker = Desk();
		ZWindow progman = Desk();
		ZWindow iconic = Live(iconic: true, width: 900, height: 700);
		ZWindow dock = Live(tool: true, tracked: true, width: 1192, height: 106);
		ZWindow dockTop = dock;
		dockTop.Topmost = true;
		ZWindow wallpaper = Live(ambient: true, width: 1920, height: 1080);
		List<ZWindow> normal = new List<ZWindow> { notepad, dock, progman, iconic };
		Expect(!DesktopCoversLive(normal), "normal z-order is not show desktop");
		Expect(LiveAppAboveDesktop(normal), "an app above the desktop means show desktop ended");
		Expect(!ShellHolding(normal), "a normal app on top is not shell holding");
		List<ZWindow> minimizedOnly = new List<ZWindow> { notepad, progman, iconic };
		Expect(!DesktopCoversLive(minimizedOnly), "iconic windows under Progman are normal");
		List<ZWindow> raised = new List<ZWindow> { edge, worker, dock, iconic, progman };
		Expect(DesktopCoversLive(raised), "desktop above the dock is show desktop even with an edge tool on top");
		Expect(!LiveAppAboveDesktop(raised), "edge tool is not a restored app");
		Expect(ShellHolding(raised), "raised desktop with iconic apps keeps the latch");
		List<ZWindow> longStack = LongStack(edge, worker, dock, iconic);
		Expect(DesktopCoversLive(longStack), "desktop past the old 64-step walk still covers");
		Expect(AnchorIndex(longStack) < 0, "no real app above a raised desktop, so do not sink");
		List<ZWindow> restored = new List<ZWindow> { notepad, edge, worker, iconic, progman };
		Expect(!DesktopCoversLive(restored), "restored app means the desktop is not covering");
		Expect(LiveAppAboveDesktop(restored), "restored app is above the desktop");
		Expect(AnchorIndex(restored) == 0, "drop just under the lowest real app, which is above the desktop");
		List<ZWindow> twoApps = new List<ZWindow> { notepad, Live(width: 800, height: 600), worker };
		Expect(AnchorIndex(twoApps) == 1, "anchor is the app directly above the desktop, so every app covers the dock");
		List<ZWindow> wallpaperHeld = new List<ZWindow> { wallpaper, edge, worker, iconic };
		Expect(!LiveAppAboveDesktop(wallpaperHeld), "wallpaper engine is not a restored app");
		Expect(ShellHolding(wallpaperHeld), "wallpaper does not clear shell holding");
		Latch held = default(Latch);
		held = Step(held, covers: true, appsBack: false, shellHolding: true);
		Expect(held.On, "cover raises");
		for (int i = 0; i < 12; i++)
		{
			held = Step(held, covers: false, appsBack: false, shellHolding: true);
		}
		Expect(held.On, "stay raised while the shell still holds minimized apps");
		held = Step(held, covers: false, appsBack: true, shellHolding: true);
		held = Step(held, covers: false, appsBack: true, shellHolding: false);
		Expect(held.On, "release waits for stable samples");
		held = Step(held, covers: false, appsBack: true, shellHolding: false);
		Expect(!held.On && held.Calm == 0, "third quiet sample releases");
		Latch again = default(Latch);
		for (int cycle = 0; cycle < 5; cycle++)
		{
			again = Step(again, covers: true, appsBack: false, shellHolding: true);
			Expect(again.On, "repeat win+d raises");
			again = Step(again, covers: false, appsBack: true, shellHolding: false);
			again = Step(again, covers: false, appsBack: true, shellHolding: false);
			again = Step(again, covers: false, appsBack: true, shellHolding: false);
			Expect(!again.On, "repeat win+d releases");
		}
		Expect(!ShouldHideIsland(showDesktop: true, manualGame: false, autoGame: false, fullscreen: true, maximized: false, showOnFullscreen: false, expanded: true, toast: false, alreadyHidden: false), "expanded island is not hidden by show desktop");
		List<ZWindow> topmostDock = new List<ZWindow> { dockTop, edge, worker, iconic, progman };
		Expect(!DesktopCoversLive(topmostDock), "temporary topmost dock is no longer covered");
		Expect(ShellHolding(topmostDock), "topmost dock does not look like the desktop went away");
	}

	private static List<ZWindow> LongStack(ZWindow edge, ZWindow worker, ZWindow dock, ZWindow iconic)
	{
		List<ZWindow> list = new List<ZWindow>(360);
		list.Add(edge);
		for (int i = 0; i < 80; i++)
		{
			list.Add(Live(tool: true, width: 40, height: 20));
		}
		list.Add(worker);
		list.Add(dock);
		for (int i = 0; i < 240; i++)
		{
			list.Add(iconic);
		}
		return list;
	}

	private static ZWindow Live(bool tool = false, bool tracked = false, bool iconic = false, bool ambient = false, int width = 200, int height = 200)
	{
		return new ZWindow
		{
			Visible = true,
			Tool = tool,
			Tracked = tracked,
			Iconic = iconic,
			Ambient = ambient,
			Width = width,
			Height = height
		};
	}

	private static ZWindow Desk()
	{
		return new ZWindow
		{
			Visible = true,
			Desktop = true,
			ShellChrome = true,
			Width = 1920,
			Height = 1080
		};
	}

	private static void Expect(bool condition, string name)
	{
		if (!condition)
		{
			throw new InvalidOperationException("ShowDesktopRules failed: " + name);
		}
	}
}
