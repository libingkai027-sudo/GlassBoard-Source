using System;

internal static class Program
{
	private static int Main()
	{
		try
		{
			GlassBoard.ShowDesktopRules.RunChecks();
			Console.WriteLine("ShowDesktopRules checks passed");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex);
			return 1;
		}
	}
}
