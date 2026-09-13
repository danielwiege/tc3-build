using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace Tc3Build.Infrastructure;

/// <summary>
/// Handles the small set of native IDE dialogs that can still be displayed
/// while Visual Studio/XAE is running in silent automation mode.
/// </summary>
internal sealed class SilentIdeDialogHandler : IDisposable
{
    private const uint ButtonClickMessage = 0x00F5;
    private readonly Action<string> log;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object processLock = new();
    private readonly HashSet<int> automationProcessIds = [];
    private readonly Thread worker;

    private SilentIdeDialogHandler(Action<string> log)
    {
        this.log = log;
        worker = new Thread(WatchWindows)
        {
            IsBackground = true,
            Name = "Tc3Build Silent IDE dialog handler"
        };
        worker.Start();
    }

    public static SilentIdeDialogHandler? Start(bool silent, Action<string> log) =>
        silent ? new SilentIdeDialogHandler(log) : null;

    public void RegisterAutomationProcess(object dte)
    {
        try
        {
            var processId = (int)((dynamic)dte).ProcessID;
            RegisterProcessId(processId);
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException)
        {
            log($"Silent dialog handler could not determine the IDE process: {exception.Message}");
        }
    }

    public void RegisterAutomationProcess(object dte, int? fallbackProcessId)
    {
        if (fallbackProcessId.HasValue)
            RegisterProcessId(fallbackProcessId.Value);

        RegisterAutomationProcess(dte);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        if (worker.IsAlive)
            worker.Join(TimeSpan.FromSeconds(2));
        cancellation.Dispose();
    }

    private void WatchWindows()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                EnumWindows(InspectWindow, IntPtr.Zero);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                log($"Silent dialog handler could not inspect IDE windows: {exception.Message}");
            }

            cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
    }

    private bool InspectWindow(IntPtr windowHandle, IntPtr _)
    {
        if (!IsWindowVisible(windowHandle))
            return true;

        GetWindowThreadProcessId(windowHandle, out var processId);
        lock (processLock)
        {
            if (!automationProcessIds.Contains((int)processId))
                return true;
        }

        var windowText = GetWindowTextValue(windowHandle);
        var childTexts = new List<(IntPtr Handle, string Text)>();
        EnumChildWindows(windowHandle, (childHandle, _) =>
        {
            var text = GetWindowTextValue(childHandle);
            if (!string.IsNullOrWhiteSpace(text))
                childTexts.Add((childHandle, text));
            return true;
        }, IntPtr.Zero);

        var allText = string.Join(" ", new[] { windowText }.Concat(childTexts.Select(item => item.Text)));
        if (ContainsAny(allText, "File Modification Detected", "modified outside the environment"))
        {
            var reloadAll = FindButton(childTexts, "Reload All");
            if (reloadAll != IntPtr.Zero)
            {
                Click(reloadAll);
                log("Silent mode answered the IDE file-modification dialog with 'Reload All'.");
            }
        }
        else if (ContainsAny(
                     allText,
                     "modified outside of TwinCAT XAE",
                     "modified outside TwinCAT XAE",
                     "Do you want to reload the project",
                     "Möchten Sie das Projekt neu laden"))
        {
            var reload = FindButton(childTexts, "Yes", "Ja", "Reload");
            if (reload != IntPtr.Zero)
            {
                Click(reload);
                log("Silent mode answered the XAE project-reload dialog with 'Yes'.");
            }
        }
        else if (ContainsAny(allText, "Save changes", "Save Changes", "Änderungen speichern"))
        {
            var doNotSave = FindButton(childTexts, "Don't Save", "Do not save", "Nicht speichern");
            if (doNotSave != IntPtr.Zero)
            {
                Click(doNotSave);
                log("Silent mode answered the IDE save dialog with 'Don't Save'.");
            }
        }

        return true;
    }

    private void RegisterProcessId(int processId)
    {
        lock (processLock)
            automationProcessIds.Add(processId);
        log($"Silent dialog handler registered IDE process {processId}.");
    }

    private static IntPtr FindButton(IEnumerable<(IntPtr Handle, string Text)> children, params string[] names)
    {
        foreach (var child in children)
        {
            if (names.Any(name => string.Equals(child.Text.Trim(), name, StringComparison.OrdinalIgnoreCase)) ||
                names.Any(name => child.Text.Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                return child.Handle;
        }

        return IntPtr.Zero;
    }

    private static void Click(IntPtr buttonHandle) =>
        SendMessage(buttonHandle, ButtonClickMessage, IntPtr.Zero, IntPtr.Zero);

    private static bool ContainsAny(string value, params string[] patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static string GetWindowTextValue(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length == 0)
            return string.Empty;

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool WindowEnumerator(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(WindowEnumerator callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, WindowEnumerator callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
