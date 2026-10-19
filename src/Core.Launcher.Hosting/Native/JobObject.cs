using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Core.Launcher.Hosting.Native;

/// <summary>Every process the launcher starts goes in here; closing the job (the launcher exiting) kills them all.</summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeKernelHandle _handle;

    public JobObject()
    {
        _handle = Kernel32.CreateJobObject(0, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
        }

        var limits = new Kernel32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = Kernel32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!Kernel32.SetInformationJobObject(_handle, Kernel32.JobObjectExtendedLimitInformation, limits, (uint)Marshal.SizeOf<Kernel32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed");
        }
    }

    public void Assign(SafeProcessHandle process)
    {
        if (!Kernel32.AssignProcessToJobObject(_handle, process))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
        }
    }

    public void Dispose() => _handle.Dispose();
}
