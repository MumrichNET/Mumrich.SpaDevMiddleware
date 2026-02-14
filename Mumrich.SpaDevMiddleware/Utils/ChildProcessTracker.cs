using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Mumrich.SpaDevMiddleware.Utils;

/// <summary>
/// Allows processes to be automatically killed when the parent process exits.
/// This works even when the parent process is killed ungracefully (e.g., debugger stop).
/// Uses Windows Job Objects on Windows, which ensures child processes are terminated
/// when the job object handle is closed (which happens automatically when the process exits).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ChildProcessTracker
{
  private static readonly Lock LOCK = new();
  private static IntPtr _jobHandle;
  private static bool _isInitialized;

  static ChildProcessTracker()
  {
    if (OperatingSystem.IsWindows())
    {
      InitializeJobObject();
    }
  }

  /// <summary>
  /// Starts a process suspended, assigns it to the job object, then resumes it.
  /// This ensures the process and ALL its children are in the job before any can escape.
  /// </summary>
  public static Process? StartProcessInJob(ProcessStartInfo aStartInfo)
  {
    if (!OperatingSystem.IsWindows())
    {
      return Process.Start(aStartInfo);
    }

    lock (LOCK)
    {
      if (!_isInitialized || _jobHandle == IntPtr.Zero)
      {
        Console.WriteLine("*** ChildProcessTracker: Job object not initialized, falling back to normal start.");
        return Process.Start(aStartInfo);
      }
    }

    // Build command line
    string commandLine = string.IsNullOrEmpty(aStartInfo.Arguments)
      ? $"\"{aStartInfo.FileName}\""
      : $"\"{aStartInfo.FileName}\" {aStartInfo.Arguments}";

    // Set up startup info for redirected I/O
    STARTUPINFO startupInfo = new() { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESTDHANDLES };

    // Set up security attributes for inheritable handles
    SECURITY_ATTRIBUTES securityAttributes = new()
    {
      nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
      bInheritHandle = true,
      lpSecurityDescriptor = IntPtr.Zero,
    };

    // Create pipes for stdin, stdout, stderr
    if (!CreatePipe(out IntPtr stdinRead, out IntPtr stdinWrite, ref securityAttributes, 0))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create stdin pipe");
    }
    SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0);

    if (!CreatePipe(out IntPtr stdoutRead, out IntPtr stdoutWrite, ref securityAttributes, 0))
    {
      CloseHandle(stdinRead);
      CloseHandle(stdinWrite);
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create stdout pipe");
    }
    SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);

    if (!CreatePipe(out IntPtr stderrRead, out IntPtr stderrWrite, ref securityAttributes, 0))
    {
      CloseHandle(stdinRead);
      CloseHandle(stdinWrite);
      CloseHandle(stdoutRead);
      CloseHandle(stdoutWrite);
      throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create stderr pipe");
    }
    SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

    startupInfo.hStdInput = stdinRead;
    startupInfo.hStdOutput = stdoutWrite;
    startupInfo.hStdError = stderrWrite;

    // Build environment block if needed
    IntPtr environment = IntPtr.Zero;
    if (aStartInfo.Environment.Count > 0 || aStartInfo.EnvironmentVariables.Count > 0)
    {
      environment = CreateEnvironmentBlock(aStartInfo);
    }

    try
    {
      // Create process suspended
      bool created = CreateProcess(
        null,
        commandLine,
        IntPtr.Zero,
        IntPtr.Zero,
        true, // inherit handles
        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
        environment,
        aStartInfo.WorkingDirectory,
        ref startupInfo,
        out PROCESS_INFORMATION processInfo
      );

      if (!created)
      {
        int error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"Failed to create process: {commandLine}");
      }

      Console.WriteLine($"*** ChildProcessTracker: Created suspended process {processInfo.dwProcessId}");

      // Assign to job BEFORE resuming
      lock (LOCK)
      {
        bool assigned = AssignProcessToJobObject(_jobHandle, processInfo.hProcess);
        if (!assigned)
        {
          int error = Marshal.GetLastWin32Error();
          Console.WriteLine(
            $"*** ChildProcessTracker: Failed to assign suspended process to job. Win32 Error: {error}"
          );
        }
        else
        {
          Console.WriteLine($"*** ChildProcessTracker: Assigned suspended process {processInfo.dwProcessId} to job.");
        }
      }

      // Resume the process
      uint resumeResult = ResumeThread(processInfo.hThread);
      if (resumeResult == unchecked((uint)-1))
      {
        int error = Marshal.GetLastWin32Error();
        Console.WriteLine($"*** ChildProcessTracker: Failed to resume process. Win32 Error: {error}");
      }
      else
      {
        Console.WriteLine($"*** ChildProcessTracker: Resumed process {processInfo.dwProcessId}");
      }

      // Close the thread handle (we don't need it)
      CloseHandle(processInfo.hThread);

      // Close the child-side handles (the child has its own copies)
      CloseHandle(stdinRead);
      CloseHandle(stdoutWrite);
      CloseHandle(stderrWrite);

      // Get the Process object
      Process process = Process.GetProcessById(processInfo.dwProcessId);

      // Attach the redirected streams using reflection (Process class doesn't expose this directly)
      // We need to use the handles we kept (stdinWrite, stdoutRead, stderrRead)
      AttachStreamsToProcess(process, stdinWrite, stdoutRead, stderrRead);

      // Close our process handle (Process object has its own)
      CloseHandle(processInfo.hProcess);

      return process;
    }
    finally
    {
      if (environment != IntPtr.Zero)
      {
        Marshal.FreeHGlobal(environment);
      }
    }
  }

  private static IntPtr CreateEnvironmentBlock(ProcessStartInfo aStartInfo)
  {
    StringBuilder envBlock = new();

    // Use EnvironmentVariables (which includes inherited + custom)
    foreach (string key in aStartInfo.EnvironmentVariables.Keys)
    {
      string? value = aStartInfo.EnvironmentVariables[key];
      if (value != null)
      {
        envBlock.Append($"{key}={value}\0");
      }
    }
    envBlock.Append('\0'); // Double null terminator

    byte[] bytes = Encoding.Unicode.GetBytes(envBlock.ToString());
    IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
    Marshal.Copy(bytes, 0, ptr, bytes.Length);
    return ptr;
  }

  private static void AttachStreamsToProcess(
    Process aProcess,
    IntPtr aStdinWrite,
    IntPtr aStdoutRead,
    IntPtr aStderrRead
  )
  {
    // Use SafeFileHandle to wrap the handles
    Microsoft.Win32.SafeHandles.SafeFileHandle stdinHandle = new(aStdinWrite, true);
    Microsoft.Win32.SafeHandles.SafeFileHandle stdoutHandle = new(aStdoutRead, true);
    Microsoft.Win32.SafeHandles.SafeFileHandle stderrHandle = new(aStderrRead, true);

    // Create StreamWriter/StreamReader from the handles
    System.IO.StreamWriter stdinStream = new(
      new System.IO.FileStream(stdinHandle, System.IO.FileAccess.Write),
      Encoding.UTF8,
      4096
    );
    System.IO.StreamReader stdoutStream = new(
      new System.IO.FileStream(stdoutHandle, System.IO.FileAccess.Read),
      Encoding.UTF8,
      true,
      4096
    );
    System.IO.StreamReader stderrStream = new(
      new System.IO.FileStream(stderrHandle, System.IO.FileAccess.Read),
      Encoding.UTF8,
      true,
      4096
    );

    // Use reflection to set the internal fields
    FieldInfo? standardInputField = typeof(Process).GetField(
      "_standardInput",
      BindingFlags.NonPublic | BindingFlags.Instance
    );
    FieldInfo? standardOutputField = typeof(Process).GetField(
      "_standardOutput",
      BindingFlags.NonPublic | BindingFlags.Instance
    );
    FieldInfo? standardErrorField = typeof(Process).GetField(
      "_standardError",
      BindingFlags.NonPublic | BindingFlags.Instance
    );

    standardInputField?.SetValue(aProcess, stdinStream);
    standardOutputField?.SetValue(aProcess, stdoutStream);
    standardErrorField?.SetValue(aProcess, stderrStream);
  }

  /// <summary>
  /// Add the process to be tracked. If the current process is killed,
  /// the child process will also be killed.
  /// Call this immediately after Process.Start() before the process spawns children.
  /// </summary>
  /// <param name="aProcess">The process to track.</param>
  public static void AddProcess(Process aProcess)
  {
    ArgumentNullException.ThrowIfNull(aProcess);

    if (!OperatingSystem.IsWindows())
    {
      return;
    }

    lock (LOCK)
    {
      if (!_isInitialized || _jobHandle == IntPtr.Zero)
      {
        Console.WriteLine("*** ChildProcessTracker: Job object not initialized, cannot track process.");

        return;
      }

      bool success = AssignProcessToJobObject(_jobHandle, aProcess.Handle);
      if (!success && !aProcess.HasExited)
      {
        int error = Marshal.GetLastWin32Error();
        Console.WriteLine(
          $"*** ChildProcessTracker: Failed to assign process {aProcess.Id} to job object. Win32 Error: {error}"
        );
      }
      else if (success)
      {
        Console.WriteLine($"*** ChildProcessTracker: Successfully assigned process {aProcess.Id} to job object.");
      }
    }
  }

  private static void InitializeJobObject()
  {
    lock (LOCK)
    {
      if (_isInitialized)
      {
        return;
      }

      _isInitialized = true;

      // Create a job object. The name must be unique per process.
      string jobName = $"ChildProcessTracker_{Environment.ProcessId}";
      _jobHandle = CreateJobObject(IntPtr.Zero, jobName);

      if (_jobHandle == IntPtr.Zero)
      {
        int error = Marshal.GetLastWin32Error();
        Console.WriteLine($"*** ChildProcessTracker: Failed to create job object. Win32 Error: {error}");

        return;
      }

      // Configure the job object to kill all processes when the job is closed.
      // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ensures that when our process exits
      // (and the job handle is automatically closed), all processes in the job are terminated.
      JobObjectExtendedLimitInformation extendedInfo = new()
      {
        BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
      };

      int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
      IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);

      try
      {
        Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

        if (
          !SetInformationJobObject(
            _jobHandle,
            JobObjectInfoType.ExtendedLimitInformation,
            extendedInfoPtr,
            (uint)length
          )
        )
        {
          int error = Marshal.GetLastWin32Error();
          Console.WriteLine($"*** ChildProcessTracker: Failed to set job object information. Win32 Error: {error}");
          CloseHandle(_jobHandle);
          _jobHandle = IntPtr.Zero;
        }
        else
        {
          Console.WriteLine($"*** ChildProcessTracker: Job object initialized successfully. Handle: {_jobHandle}");
        }
      }
      finally
      {
        Marshal.FreeHGlobal(extendedInfoPtr);
      }
    }
  }

  #region Windows API

  private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
  private const uint CREATE_SUSPENDED = 0x00000004;
  private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
  private const uint STARTF_USESTDHANDLES = 0x00000100;
  private const uint HANDLE_FLAG_INHERIT = 0x00000001;

  [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
  private static extern IntPtr CreateJobObject(IntPtr aSecurityAttributes, string? aName);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(
    IntPtr aJobHandle,
    JobObjectInfoType aInfoType,
    IntPtr aJobObjectInfo,
    uint aJobObjectInfoLength
  );

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(IntPtr aJobHandle, IntPtr aProcessHandle);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr aHandle);

  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreateProcess(
    string? aApplicationName,
    string aCommandLine,
    IntPtr aProcessAttributes,
    IntPtr aThreadAttributes,
    bool aInheritHandles,
    uint aCreationFlags,
    IntPtr aEnvironment,
    string? aCurrentDirectory,
    ref STARTUPINFO aStartupInfo,
    out PROCESS_INFORMATION aProcessInformation
  );

  [DllImport("kernel32.dll", SetLastError = true)]
  private static extern uint ResumeThread(IntPtr aThreadHandle);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CreatePipe(
    out IntPtr aReadHandle,
    out IntPtr aWriteHandle,
    ref SECURITY_ATTRIBUTES aPipeAttributes,
    uint aSize
  );

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetHandleInformation(IntPtr aObject, uint aMask, uint aFlags);

  private enum JobObjectInfoType
  {
    ExtendedLimitInformation = 9,
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct SECURITY_ATTRIBUTES
  {
    public int nLength;
    public IntPtr lpSecurityDescriptor;

    [MarshalAs(UnmanagedType.Bool)]
    public bool bInheritHandle;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct STARTUPINFO
  {
    public int cb;
    public string lpReserved;
    public string lpDesktop;
    public string lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public uint dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct PROCESS_INFORMATION
  {
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectBasicLimitInformation
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
  private struct IoCounters
  {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectExtendedLimitInformation
  {
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }

  #endregion
}
