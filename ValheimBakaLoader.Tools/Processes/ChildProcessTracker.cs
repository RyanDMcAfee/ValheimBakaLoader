using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ValheimBakaLoader.Tools.Processes
{
    /// <summary>
    /// Ensures child processes are killed when this application exits, even if
    /// BakaLoader is terminated via Task Manager or crashes. Uses a Windows Job
    /// Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: when the last handle to
    /// the job is closed (i.e. our process dies), Windows force-kills every
    /// process in the job.
    /// </summary>
    public static class ChildProcessTracker
    {
        private static readonly IntPtr JobHandle;

        static ChildProcessTracker()
        {
            // Create a uniquely-named job object tied to this BakaLoader instance.
            var jobName = "BakaLoader_ChildJob_" + Environment.ProcessId;
            JobHandle = CreateJobObject(IntPtr.Zero, jobName);
            if (JobHandle == IntPtr.Zero) return;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var infoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                SetInformationJobObject(JobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }

        /// <summary>
        /// Assigns a process to the job object so it is automatically killed when
        /// BakaLoader exits. Safe to call on an already-exited process (no-op).
        /// </summary>
        public static void AddProcess(Process process)
        {
            if (JobHandle == IntPtr.Zero) return;

            try
            {
                if (!process.HasExited)
                {
                    AssignProcessToJobObject(JobHandle, process.Handle);
                }
            }
            catch
            {
                // Process may have exited between the check and the call - harmless.
            }
        }

        #region Win32 interop

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
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

        #endregion
    }
}
