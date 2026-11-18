using System.Runtime.InteropServices;

// These APIs let the test check whether the replacement is still attached to the launch console.
public static class WindowsConsoleApi
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleProcessList([Out] uint[] processIds, uint processCount);
}
