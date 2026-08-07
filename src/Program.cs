using System.Runtime.InteropServices;
using InventoryTracker.App;

namespace InventoryTracker;

internal static class Program
{
    /// <summary>Hand back to the console we were launched from, if there is one.</summary>
    private const int AttachParentProcess = -1;

    // DllImport rather than LibraryImport: the source generator would force
    // AllowUnsafeBlocks on the whole project for this single call.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        // The tray app is a WinExe so it never flashes a console window. That also means
        // Console output goes nowhere unless we reattach, which the CLI paths need.
        var tray = args.Length == 0 || args[0] == "--tray";
        if (!tray)
        {
            AttachConsole(AttachParentProcess);
            return Cli.RunAsync(args).GetAwaiter().GetResult();
        }

        return TrayHost.Run(args.Skip(1).ToArray());
    }
}
