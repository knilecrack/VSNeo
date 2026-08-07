using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Ties a child process's lifetime to ours, so it cannot outlive Visual Studio.
    ///
    /// Under the old stdio transport nvim died on its own: closing the pipe gave it
    /// EOF on stdin and it exited. --listen has no stdin to close, so a devenv that
    /// crashes or is killed leaves a headless nvim running forever, and they
    /// accumulate one per crash. Dispose is not enough on its own - it never runs
    /// when the process is killed outright.
    ///
    /// A job object with KILL_ON_JOB_CLOSE is the operating system's answer: the
    /// handle dies with the process whatever the cause, and Windows terminates
    /// everything assigned to the job.
    /// </summary>
    internal sealed class ProcessJob : IDisposable
        {
        private IntPtr _handle;

        private ProcessJob(IntPtr handle) => _handle = handle;

        /// <summary>
        /// Returns null when the job could not be created. That is not fatal: it
        /// means a crash may orphan an nvim, not that anything stops working.
        /// </summary>
        public static ProcessJob TryAssign(Process process)
        {
            IntPtr job = IntPtr.Zero;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return null;

                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, buffer, false);
                    if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size))
                    {
                        CloseHandle(job);
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    // Already in a job that disallows nesting, most likely. Harmless.
                    Log.Write("could not assign nvim to a job object; it may outlive a crash");
                    CloseHandle(job);
                    return null;
                }

                return new ProcessJob(job);
            }
            catch (Exception ex)
            {
                if (job != IntPtr.Zero) CloseHandle(job);
                Log.Write("job object setup failed", ex);
                return null;
            }
        }

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int ExtendedLimitInformation = 9;

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
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
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
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
