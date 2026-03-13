using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Akka.Actor;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Mumrich.SpaDevMiddleware.Actors.SpaDevServer.Commands;
using Mumrich.SpaDevMiddleware.Actors.SpaDevServer.Responses;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Extensions;
using Mumrich.SpaDevMiddleware.SignalR;
using Mumrich.SpaDevMiddleware.Utils;

namespace Mumrich.SpaDevMiddleware.Actors.SpaDevServer;

/// <summary>
/// The parent of a Node.js dev-server.
/// </summary>
public class SpaDevServerActor : FSM<SpaDevServerActorState, ISpaDevServerActorData>, IWithTimers
{
  private const string DEFAULT_REGEX = "running at";
  private const int MAX_NBR_RETRYS = 10;
  private const int RETRY_BACKOFF_BASE_SECONDS = 5;
  private const int RETRY_BACKOFF_SECONDS_PER_ATTEMPT = 5;
  private static readonly Regex ANSI_COLOR_REGEX = new(
    "\x001b\\[[0-9;]*m",
    RegexOptions.None,
    TimeSpan.FromSeconds(1)
  );

  private static readonly TimeSpan REGEX_MATCH_TIMEOUT = TimeSpan.FromMinutes(5);

  // Per-evaluation timeout prevents catastrophic backtracking on a single log line.
  private static readonly TimeSpan REGEX_EVAL_TIMEOUT = TimeSpan.FromSeconds(1);

  private static readonly JsonSerializerOptions DEFAULT_JSON_SERIALIZER_OPTIONS =
    new JsonSerializerOptions { WriteIndented = true };
  private readonly ILogger<SpaDevServerActor>? _logger;
  private readonly IHubContext<SpaDevServerLogHub, ISpaDevServerLogHub>? _spaDevServerLogHub;
  private int _nbrRetrys = 0;

  public SpaDevServerActor(
    IServiceProvider aServiceProvider,
    string aBasePublicPath,
    SpaSettings aSpaSettings
  )
  {
    IServiceScope serviceProviderScope = aServiceProvider.CreateScope();
    _logger = serviceProviderScope.ServiceProvider.GetService<ILogger<SpaDevServerActor>>();
    _spaDevServerLogHub = serviceProviderScope.ServiceProvider.GetService<
      IHubContext<SpaDevServerLogHub, ISpaDevServerLogHub>
    >();

    StartWith(
      SpaDevServerActorState.Stopped,
      new SpaDevServerActorData { SpaSettings = aSpaSettings, BasePublicPath = aBasePublicPath }
    );

    When(
      SpaDevServerActorState.Stopped,
      aContext =>
        aContext.FsmEvent switch
        {
          StartProcessCommand => StartProcessCommandHandle(aContext.StateData),
          StdOutMatchResponse aStdOutMatchResponse => StdOutMatchResponseHandle(
            aStdOutMatchResponse.Success,
            aStdOutMatchResponse.Exception
          ),
          _ => null,
        }
    );

    When(
      SpaDevServerActorState.Started,
      aContext =>
        aContext.FsmEvent switch
        {
          // Terminal state: dev-server is running; no further FSM transitions defined.
          _ => null,
        }
    );

    When(
      SpaDevServerActorState.Error,
      aContext =>
        aContext.FsmEvent switch
        {
          // Terminal state: unrecoverable error after MAX_NBR_RETRYS exhausted.
          _ => null,
        }
    );
  }

  protected override void PreStart()
  {
    Self.Tell(new StartProcessCommand());
  }

  public ITimerScheduler? Timers { get; set; }

  private Process? RunnerProcess { get; set; }

  private EventedStreamReader? StdErr { get; set; }

  private EventedStreamReader? StdOut { get; set; }

  protected override void PostStop()
  {
    if (RunnerProcess?.HasExited == false)
    {
      try
      {
        RunnerProcess.Kill(entireProcessTree: true);
      }
      catch (Exception ex)
      {
        _logger?.LogWarning(ex, "Failed to kill dev-server process during actor shutdown");
      }
    }
  }

  private static Process? LaunchNodeProcess(ProcessStartInfo aStartInfo)
  {
    try
    {
      Process? process = Process.Start(aStartInfo);

      if (process != null)
      {
        process.EnableRaisingEvents = true;
      }

      return process;
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(BuildNodeLaunchErrorMessage(aStartInfo.FileName), ex);
    }
  }

  /// <summary>
  /// Launches a node process and assigns it to the job object for proper cleanup.
  /// Uses CREATE_SUSPENDED to ensure the process is in the job before it spawns children.
  /// </summary>
  private static Process? LaunchNodeProcessWithJobTracking(ProcessStartInfo aStartInfo)
  {
    if (!OperatingSystem.IsWindows())
    {
      return LaunchNodeProcess(aStartInfo);
    }

    try
    {
      Process? process = ChildProcessTracker.StartProcessInJob(aStartInfo);

      if (process != null)
      {
        process.EnableRaisingEvents = true;
      }

      return process;
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException(BuildNodeLaunchErrorMessage(aStartInfo.FileName), ex);
    }
  }

  private static string BuildNodeLaunchErrorMessage(string aExecutableName) =>
    $"Failed to start '{aExecutableName}'. To resolve this:.\n\n"
    + $"[1] Ensure that '{aExecutableName}' is installed and can be found in one of the PATH directories.\n"
    + $"    Current PATH enviroment variable is: {Environment.GetEnvironmentVariable("PATH")}\n"
    + "    Make sure the executable is in one of those directories, or update your PATH.\n\n"
    + "[2] See the InnerException for further details of the cause.";

  private static string StripAnsiColors(string aLine) =>
    ANSI_COLOR_REGEX.Replace(aLine, string.Empty);

  private void AttachToLogger(
    string aSpaDevServerName,
    IHubContext<SpaDevServerLogHub, ISpaDevServerLogHub>? aSpaDevServerLogHub
  )
  {
    // When the NPM task emits complete lines, pass them through to the real logger
    StdOut!.OnReceivedLine += aLine => ForwardLogLine(aSpaDevServerName, aSpaDevServerLogHub, aLine, aIsError: false);
    StdErr!.OnReceivedLine += aLine => HandleStdErrLine(aSpaDevServerName, aSpaDevServerLogHub, aLine);

    // But when it emits incomplete lines, assume this is progress information and
    // hence just pass it through to StdOut regardless of logger config.
    StdErr.OnReceivedChunk += HandleStdErrChunk;
  }

  private void HandleStdErrLine(
    string aSpaDevServerName,
    IHubContext<SpaDevServerLogHub, ISpaDevServerLogHub>? aSpaDevServerLogHub,
    string aLine
  )
  {
    if (string.IsNullOrWhiteSpace(aLine))
    {
      return;
    }

    string effectiveLine = aLine.StartsWith("<s>") ? aLine[3..] : aLine;
    ForwardLogLine(aSpaDevServerName, aSpaDevServerLogHub, effectiveLine, aIsError: true);
  }

  private void HandleStdErrChunk(ArraySegment<char> aChunk)
  {
    if (aChunk.Array == null)
    {
      return;
    }

    bool containsNewline = Array.IndexOf(aChunk.Array, '\n', aChunk.Offset, aChunk.Count) >= 0;
    if (!containsNewline)
    {
      _logger?.LogInformation("{Chunk}", new string(aChunk.Array));
    }
  }

  // NPM tasks commonly emit ANSI colors, but forwarding raw ANSI sequences to
  // structured logs is noisy and often unreadable outside terminals.
  private void ForwardLogLine(
    string aSpaDevServerName,
    IHubContext<SpaDevServerLogHub, ISpaDevServerLogHub>? aSpaDevServerLogHub,
    string aLine,
    bool aIsError
  )
  {
    aSpaDevServerLogHub?.Clients.All.ReceiveLogEntry(aSpaDevServerName, aLine, aIsError: aIsError);
    string effectiveLine = StripAnsiColors(aLine).TrimEnd('\n');

    if (_logger == null)
    {
      WriteToConsoleFallback(aSpaDevServerName, aLine, aIsError);

      return;
    }

    if (aIsError)
    {
      _logger.LogError("[{SpaDevServerName}]: {EffectiveLine}", aSpaDevServerName, effectiveLine);

      return;
    }

    _logger.LogInformation("[{SpaDevServerName}]: {EffectiveLine}", aSpaDevServerName, effectiveLine);
  }

  private static void WriteToConsoleFallback(string aSpaDevServerName, string aLine, bool aIsError)
  {
    if (aIsError)
    {
      Console.Error.WriteLine($"[{aSpaDevServerName}]: {aLine}");

      return;
    }

    Console.WriteLine($"[{aSpaDevServerName}]: {aLine}");
  }

  private State<SpaDevServerActorState, ISpaDevServerActorData> StartProcessCommandHandle(
    ISpaDevServerActorData aSpaMiddlewareActorData
  )
  {
    SpaSettings? spaSettings = aSpaMiddlewareActorData?.SpaSettings;
    string? basePublicPath = aSpaMiddlewareActorData?.BasePublicPath;
    string? regex = spaSettings?.Regex;

    if (spaSettings == null)
    {
      _logger?.LogError("{SpaSettings} is null", nameof(spaSettings));

      return GoTo(SpaDevServerActorState.Error);
    }

    if (basePublicPath == null)
    {
      _logger?.LogError("{BasePublicPath} is null", nameof(basePublicPath));

      return GoTo(SpaDevServerActorState.Error);
    }

    _logger?.LogInformation(
      "BasePublicPath: {BasePublicPath}, SpaSettings: {SpaSettings}",
      basePublicPath,
      JsonSerializer.Serialize(spaSettings, DEFAULT_JSON_SERIALIZER_OPTIONS)
    );

    spaSettings.Environment.TryAdd("BASE_PUBLIC_PATH", basePublicPath);

    ProcessStartInfo? processStartInfo = spaSettings?.GetProcessStartInfo();

    if (processStartInfo == null)
    {
      _logger?.LogError("{ProcessStartInfo} is null", nameof(processStartInfo));

      return GoTo(SpaDevServerActorState.Error);
    }

    _logger?.LogInformation(
      "{ProcessStartInfo}: {FileName} {Arguments} (cwd: '{WorkingDirectory}')",
      nameof(processStartInfo),
      processStartInfo.FileName,
      processStartInfo.Arguments,
      processStartInfo.WorkingDirectory
    );

    RunnerProcess = LaunchNodeProcessWithJobTracking(processStartInfo);

    if (RunnerProcess == null)
    {
      _logger?.LogError("{RunnerProcess} is null", nameof(RunnerProcess));

      return GoTo(SpaDevServerActorState.Error);
    }

    StdOut = new EventedStreamReader(RunnerProcess.StandardOutput);
    StdErr = new EventedStreamReader(RunnerProcess.StandardError);

    AttachToLogger(spaSettings?.SpaRootPath ?? "??", _spaDevServerLogHub);

    using EventedStreamStringReader stdErrReader = new EventedStreamStringReader(StdErr);

    // Wait for the dev-server startup message with an overall deadline.
    // REGEX_EVAL_TIMEOUT limits per-line regex evaluation (guards against catastrophic backtracking).
    // REGEX_MATCH_TIMEOUT is the overall startup deadline; if the server never prints the expected
    // string within that window the actor transitions to the Error state and may retry.
    Task<Match> matchTask = StdOut.WaitForMatch(
      new Regex(
        !string.IsNullOrWhiteSpace(regex) ? regex : DEFAULT_REGEX,
        RegexOptions.None,
        REGEX_EVAL_TIMEOUT
      )
    );

    Task<Match> timeoutTask = Task.Delay(REGEX_MATCH_TIMEOUT)
      .ContinueWith<Match>(
        _ => throw new TimeoutException(
          $"Dev-server '{spaSettings?.SpaRootPath ?? "?"}' did not output the expected startup string within {REGEX_MATCH_TIMEOUT.TotalMinutes} minutes."
        ),
        TaskScheduler.Default
      );

    Task.WhenAny(matchTask, timeoutTask)
      .Unwrap()
      .PipeTo(
        Self,
        Self,
        () => new StdOutMatchResponse(true),
        (aException) => new StdOutMatchResponse(false, aException)
      );

    return Stay();
  }

  private State<SpaDevServerActorState, ISpaDevServerActorData> StdOutMatchResponseHandle(
    bool aSuccess,
    Exception? aException = null
  )
  {
    if (aSuccess)
    {
      _logger?.LogInformation("*** SPA Dev-Server appears to be ready!");

      return GoTo(SpaDevServerActorState.Started);
    }

    _logger?.LogError(aException, "*** StdOut could not match!");

    if (_nbrRetrys < MAX_NBR_RETRYS)
    {
      ++_nbrRetrys;

      Timers?.StartSingleTimer(
        nameof(StartProcessCommand),
        new StartProcessCommand(),
        TimeSpan.FromSeconds(RETRY_BACKOFF_BASE_SECONDS + RETRY_BACKOFF_SECONDS_PER_ATTEMPT * _nbrRetrys)
      );

      return Stay();
    }
    else
    {
      _logger?.LogError("*** Max Number retries reached!");

      return GoTo(SpaDevServerActorState.Error);
    }
  }
}
