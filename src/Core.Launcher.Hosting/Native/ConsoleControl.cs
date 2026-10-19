namespace Core.Launcher.Hosting.Native;

internal static class ConsoleControl
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Sends Ctrl+Break to a process started in its own process group with a console of its own. The launcher has no
    /// console, so it attaches to the child's for the call; a launcher that already has one (a test host) can't.
    /// </summary>
    public static bool SendCtrlBreak(int processGroupId)
    {
        lock (Gate)
        {
            if (Kernel32.GetConsoleWindow() != 0 || !Kernel32.AttachConsole((uint)processGroupId))
            {
                return false;
            }

            try
            {
                return Kernel32.GenerateConsoleCtrlEvent(Kernel32.CTRL_BREAK_EVENT, (uint)processGroupId);
            }
            finally
            {
                Kernel32.FreeConsole();
            }
        }
    }
}
