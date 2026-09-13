using System.Runtime.InteropServices;

namespace Tc3Build.Infrastructure;

/// <summary>
/// Retries COM calls while Visual Studio/TwinCAT is busy processing an earlier
/// automation call. Beckhoff recommends this filter for Automation Interface
/// clients running in an STA.
/// </summary>
internal static class ComMessageFilter
{
    private static IOleMessageFilter? registeredFilter;

    public static void Register()
    {
        if (registeredFilter is not null)
            return;

        var filter = new OleMessageFilter();
        var result = CoRegisterMessageFilter(filter, out _);
        if (result != 0)
            throw new COMException("Could not register the COM message filter.", result);

        // Keep the managed COM callable wrapper alive for the complete STA
        // lifetime. COM otherwise has no object to call back into.
        registeredFilter = filter;
    }

    public static void Revoke()
    {
        if (registeredFilter is null)
            return;

        try
        {
            CoRegisterMessageFilter(null, out _);
        }
        finally
        {
            registeredFilter = null;
        }
    }

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
    }

    private sealed class OleMessageFilter : IOleMessageFilter
    {
        public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) =>
            0; // SERVERCALL_ISHANDLED

        public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType) =>
            rejectType == 2 ? 99 : -1; // SERVERCALL_RETRYLATER: retry in 99 ms

        public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) =>
            2; // PENDINGMSG_WAITDEFPROCESS
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(
        IOleMessageFilter? newFilter,
        out IOleMessageFilter? oldFilter);
}
