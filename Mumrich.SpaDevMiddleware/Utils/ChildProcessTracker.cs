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

    string commandLine = BuildCommandLine(aStartInfo);
    STARTUPINFO startupInfo = CreateStartupInfoForRedirectedIo();
    SECURITY_ATTRIBUTES securityAttributes = CreateInheritableSecurityAttributes();
    RedirectedPipeHandles pipeHandles = RedirectedPipeHandles.Create(ref securityAttributes);
    pipeHandles.ApplyTo(ref startupInfo);

    IntPtr environment = CreateEnvironmentBlockIfNeeded(aStartInfo);
    PROCESS_INFORMATION processInfo = default;

    try
    {
      processInfo = CreateSuspendedProcess(commandLine, environment, aStartInfo.WorkingDirectory, ref startupInfo);

      Console.WriteLine($"*** ChildProcessTracker: Created suspended process {processInfo.dwProcessId}");

      AssignProcessToJob(processInfo);
      ResumeCreatedProcess(processInfo);

      // No longer needed once resumed.
      CloseHandleIfSet(ref processInfo.hThread);

      // Child has its own copies now.
      pipeHandles.CloseChildHandles();

      Process process = Process.GetProcessById(processInfo.dwProcessId);
      AttachStreamsToProcess(process, pipeHandles.StdinWrite, pipeHandles.StdoutRead, pipeHandles.StderrRead);

      // Stream wrappers now own these handles.
      pipeHandles.MarkParentHandlesTransferred();

      // Process object has its own handle.
      CloseHandleIfSet(ref processInfo.hProcess);

      return process;
    }
    finally
    {
      if (environment != IntPtr.Zero)
      {
        Marshal.FreeHGlobal(environment);
      }

      pipeHandles.CloseChildHandles();
      pipeHandles.CloseParentHandles();

      CloseHandleIfSet(ref processInfo.hThread);
      CloseHandleIfSet(ref processInfo.hProcess);
    }
  }

  private static string BuildCommandLine(ProcessStartInfo aStartInfo)
  {
    return string.IsNullOrEmpty(aStartInfo.Arguments)
      ? $"\"{aStartInfo.FileName}\""
      : $"\"{aStartInfo.FileName}\" {aStartInfo.Arguments}";
  }

  private static STARTUPINFO CreateStartupInfoForRedirectedIo()
  {
    return new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESTDHANDLES };
  }

  private static SECURITY_ATTRIBUTES CreateInheritableSecurityAttributes()
  {
    return new SECURITY_ATTRIBUTES
    {
      nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
      bInheritHandle = true,
      lpSecurityDescriptor = IntPtr.Zero,
    };
  }

  private static IntPtr CreateEnvironmentBlockIfNeeded(ProcessStartInfo aStartInfo)
  {
    if (aStartInfo.Environment.Count == 0 && aStartInfo.EnvironmentVariables.Count == 0)
    {
      return IntPtr.Zero;
    }

    return CreateEnvironmentBlock(aStartInfo);
  }

  private static PROCESS_INFORMATION CreateSuspendedProcess(
    string aCommandLine,
    IntPtr aEnvironment,
    string? aWorkingDirectory,
    ref STARTUPINFO aStartupInfo
  )
  {
    bool created = CreateProcess(
      null,
      aCommandLine,
      IntPtr.Zero,
      IntPtr.Zero,
      true,
      CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
      aEnvironment,
      aWorkingDirectory,
      ref aStartupInfo,
      out PROCESS_INFORMATION processInfo
    );

    if (!created)
    {
      int error = Marshal.GetLastWin32Error();
      throw new Win32Exception(error, $"Failed to create process: {aCommandLine}");
    }

    return processInfo;
  }

  private static void AssignProcessToJob(PROCESS_INFORMATION aProcessInfo)
  {
    lock (LOCK)
    {
      bool assigned = AssignProcessToJobObject(_jobHandle, aProcessInfo.hProcess);
      if (!assigned)
      {
        int error = Marshal.GetLastWin32Error();
        Console.WriteLine($"*** ChildProcessTracker: Failed to assign suspended process to job. Win32 Error: {error}");
      }
      else
      {
        Console.WriteLine($"*** ChildProcessTracker: Assigned suspended process {aProcessInfo.dwProcessId} to job.");
      }
    }
  }

  private static void ResumeCreatedProcess(PROCESS_INFORMATION aProcessInfo)
  {
    uint resumeResult = ResumeThread(aProcessInfo.hThread);
    if (resumeResult == unchecked((uint)-1))
    {
      int error = Marshal.GetLastWin32Error();
      Console.WriteLine($"*** ChildProcessTracker: Failed to resume process. Win32 Error: {error}");
    }
    else
    {
      Console.WriteLine($"*** ChildProcessTracker: Resumed process {aProcessInfo.dwProcessId}");
    }
  }

  private static void CloseHandleIfSet(ref IntPtr aHandle)
  {
    if (aHandle == IntPtr.Zero)
    {
      return;
    }

    CloseHandle(aHandle);
    aHandle = IntPtr.Zero;
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

    // Use reflection to set the internal fields.
    // The .NET Process class provides no public API to attach pre-created stream handles after
    // a process has been started with CreateProcess/CREATE_SUSPENDED. These private field names
    // (_standardInput, _standardOutput, _standardError) have been stable since .NET Core 1.0;
    // verify them against the .NET runtime source if upgrading the target framework.
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

  private sealed class RedirectedPipeHandles
  {
    public IntPtr StdinRead;
    public IntPtr StdinWrite;
    public IntPtr StdoutRead;
    public IntPtr StdoutWrite;
    public IntPtr StderrRead;
    public IntPtr StderrWrite;

    public static RedirectedPipeHandles Create(ref SECURITY_ATTRIBUTES aSecurityAttributes)
    {
      RedirectedPipeHandles handles = new();
      try
      {
        (handles.StdinRead, handles.StdinWrite) = CreateInheritablePipe(
          ref aSecurityAttributes,
          parentKeepsReadEnd: false,
          "stdin"
        );
        (handles.StdoutRead, handles.StdoutWrite) = CreateInheritablePipe(
          ref aSecurityAttributes,
          parentKeepsReadEnd: true,
          "stdout"
        );
        (handles.StderrRead, handles.StderrWrite) = CreateInheritablePipe(
          ref aSecurityAttributes,
          parentKeepsReadEnd: true,
          "stderr"
        );

        return handles;
      }
      catch
      {
        handles.CloseChildHandles();
        handles.CloseParentHandles();
        throw;
      }
    }

    public void ApplyTo(ref STARTUPINFO aStartupInfo)
    {
      aStartupInfo.hStdInput = StdinRead;
      aStartupInfo.hStdOutput = StdoutWrite;
      aStartupInfo.hStdError = StderrWrite;
    }

    public void CloseChildHandles()
    {
      CloseHandleIfSet(ref StdinRead);
      CloseHandleIfSet(ref StdoutWrite);
      CloseHandleIfSet(ref StderrWrite);
    }

    public void CloseParentHandles()
    {
      CloseHandleIfSet(ref StdinWrite);
      CloseHandleIfSet(ref StdoutRead);
      CloseHandleIfSet(ref StderrRead);
    }

    public void MarkParentHandlesTransferred()
    {
      StdinWrite = IntPtr.Zero;
      StdoutRead = IntPtr.Zero;
      StderrRead = IntPtr.Zero;
    }

    /// <summary>
    /// Creates a pipe where the child-facing handle is inheritable and the parent-facing handle is not.
    /// </summary>
    private static (IntPtr read, IntPtr write) CreateInheritablePipe(
      ref SECURITY_ATTRIBUTES aSecurityAttributes,
      bool parentKeepsReadEnd,
      string aPipeName
    )
    {
      if (!CreatePipe(out IntPtr read, out IntPtr write, ref aSecurityAttributes, 0))
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create {aPipeName} pipe");

      // Remove the inherit flag from the handle that stays with the parent process
      SetHandleInformation(parentKeepsReadEnd ? read : write, HANDLE_FLAG_INHERIT, 0);

      return (read, write);
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

      int length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
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
