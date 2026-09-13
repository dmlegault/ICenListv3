using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Enlist.Agent.Hosting;

// ------------------------------------------------------------------------------------------------
// Ported from enList v2 (src/AppHost/Hosting/JobObject.cs) unchanged in mechanism, just re-homed —
// the underlying Win32 API has never had a managed wrapper on either .NET Framework or .NET Core, so
// this is plain P/Invoke either way.
//
// A Win32 Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Every enlist-runner.exe child process
// gets assigned to this job right after Process.Start(). Windows then guarantees that if the AGENT's
// job handle goes away for ANY reason — clean shutdown, crash, `taskkill /f`, power loss — every
// process still assigned to the job is forcibly terminated by the OS.
//
// Why this matters MORE here than it did for v2's AppHost: the agent<->runner protocol is a named
// pipe, which dies with the process that created it. A runner that survives the agent's death is not
// a resilient orphan quietly carrying on — it's unreachable (no agent can ever talk to it again over
// that pipe) and invisible to a restarted agent, which will read its assignments and spawn a SECOND
// runner for the same application, risking two live instances of the same service at once (e.g. both
// trying to bind the same port). Killing orphans on agent death turns that into a clean restart
// instead of silent duplication.
// ------------------------------------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    /// <summary>Cleared to Zero by Dispose, so the handle is closed exactly once - see Dispose. Not readonly for that reason alone.</summary>
    private IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create Job Object (CreateJobObject returned NULL).");
        }

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var extendedInfoPtr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

            if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length))
            {
                throw new InvalidOperationException(
                    $"Failed to set Job Object information (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    /// <summary>
    /// Binds a process to this job. Once assigned, the process is killed automatically if this job's
    /// handle is closed (i.e. when the owning agent process exits, however it exits). Safe to call
    /// more than once for the same process; safe to no-op if the process already exited.
    /// </summary>
    public bool AssignProcess(IntPtr processHandle)
    {
        return AssignProcessToJobObject(_handle, processHandle);
    }

    public void Dispose()
    {
        // The handle is cleared as it is closed, so a second Dispose is a no-op rather than a second
        // CloseHandle on the same value. That matters more here than almost anywhere else in the
        // agent: this handle is what kills every runner when the agent dies, and Windows recycles
        // handle values - closing a stale one can close whatever now holds that number.
        //
        // Reachable, not theoretical: AgentHost.StopAsync disposes it, and DisposeAsync used to call
        // StopAsync again.
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
