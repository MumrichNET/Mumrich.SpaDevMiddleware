using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

using Akka.Actor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Mumrich.HelpersAndExtensions;
using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Extensions;
using Mumrich.SpaDevMiddleware.Utils;

using Newtonsoft.Json;

namespace Mumrich.SpaDevMiddleware.Actors
{
  public record StartProcessCommand;

  public class SpaDevServerRunnerActor : ReceiveActor, IDisposable
  {
    private const string DefaultRegex = "running at";
    private static readonly Regex AnsiColorRegex = new("\x001b\\[[0-9;]*m", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<SpaDevServerRunnerActor> _logger;
    private readonly SpaSettings _spaSettings;
    private bool _isDisposed;

    public SpaDevServerRunnerActor(IServiceProvider serviceProvider, SpaSettings spaSettings)
    {
      var serviceProviderScope = serviceProvider.CreateScope();
      var spaDevServerSettings = serviceProviderScope.ServiceProvider.GetService<ISpaDevServerSettings>();
      _logger = serviceProviderScope.ServiceProvider.GetService<ILogger<SpaDevServerRunnerActor>>();

      var regex = spaSettings.Regex;

      _logger.LogInformation("SpaSettings: {}", JsonConvert.SerializeObject(spaSettings, Formatting.Indented));

      var processStartInfo = spaSettings.GetProcessStartInfo(spaDevServerSettings);

      _logger.LogInformation("{}: {} {} (cwd: '{}')", nameof(processStartInfo), processStartInfo.FileName, processStartInfo.Arguments, processStartInfo.WorkingDirectory);

      ReceiveAsync<StartProcessCommand>(async _ =>
      {
        RunnerProcess = LaunchNodeProcess(processStartInfo);

        StdOut = new EventedStreamReader(RunnerProcess.StandardOutput);
        StdErr = new EventedStreamReader(RunnerProcess.StandardError);

        AttachToLogger();

        using var stdErrReader = new EventedStreamStringReader(StdErr);

        try
        {
          // Although the dev server may eventually tell us the URL it's listening on,
          // it doesn't do so until it's finished compiling, and even then only if there were
          // no compiler warnings. So instead of waiting for that, consider it ready as soon
          // as it starts listening for requests.
          await StdOut.WaitForMatch(new Regex(
            !string.IsNullOrWhiteSpace(regex) ? regex : DefaultRegex,
            RegexOptions.None,
            RegexMatchTimeout));
        }
        catch (EndOfStreamException ex)
        {
          throw new InvalidOperationException(
            $"The Command '{spaSettings.NodeStartScript}' exited without indicating that the " +
            "server was listening for requests. The error output was: " +
            $"{stdErrReader.ReadAsString()}", ex);
        }
      });

      Self.Tell(new StartProcessCommand());
      _spaSettings = spaSettings;
    }

    ~SpaDevServerRunnerActor()
    {
      Dispose(disposing: false);
    }

    private Process RunnerProcess { get; set; }

    private EventedStreamReader StdErr { get; set; }

    private EventedStreamReader StdOut { get; set; }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (_isDisposed)
      {
        return;
      }

      _lock.Wait();

      try
      {
        if (_isDisposed)
        {
          return;
        }

        if (disposing) // If this method was called by a finalizer, we shouldn't try to release managed resources - https://docs.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose#the-disposeboolean-overload
        {
          // A finalizer can run even if an object's constructor never completed, to be safe, use null conditional operator.
          if (RunnerProcess?.HasExited == false)
          {
            try
            {
              RunnerProcess?.KillProcessTree();
            }
            catch
            {
              // Throws if process is already dead, note that process could die between HasExited check and Kill
            }
          }
          RunnerProcess?.Dispose();
        }

        _isDisposed = true;
      }
      finally
      {
        _lock.Release();
      }
    }

    protected override void PostStop()
    {
      RunnerProcess.KillProcessTree();
    }

    private static Process LaunchNodeProcess(ProcessStartInfo startInfo)
    {
      try
      {
        var process = Process.Start(startInfo);

        if (process != null)
        {
          process.EnableRaisingEvents = true;
        }

        return process;
      }
      catch (Exception ex)
      {
        var message = $"Failed to start '{startInfo.FileName}'. To resolve this:.\n\n"
                      + $"[1] Ensure that '{startInfo.FileName}' is installed and can be found in one of the PATH directories.\n"
                      + $"    Current PATH enviroment variable is: {Environment.GetEnvironmentVariable("PATH")}\n"
                      + "    Make sure the executable is in one of those directories, or update your PATH.\n\n"
                      + "[2] See the InnerException for further details of the cause.";
        throw new InvalidOperationException(message, ex);
      }
    }

    private static string StripAnsiColors(string line) => AnsiColorRegex.Replace(line, string.Empty);

    private void AttachToLogger()
    {
      void OnReceivedLine(string line)
      {
        if (_logger == null)
        {
          Console.WriteLine($"{_spaSettings.DevServerAddress}: {line}");
        }
        else
        {
          var effectiveLine = StripAnsiColors(line).TrimEnd('\n');
          _logger.LogInformation("{}: {}", _spaSettings.DevServerAddress, effectiveLine);
        }
      }

      void OnReceivedChunk(ArraySegment<char> chunk)
      {
        if (chunk.Array == null)
        {
          return;
        }

        var containsNewline = Array.IndexOf(chunk.Array, '\n', chunk.Offset, chunk.Count) >= 0;

        if (!containsNewline)
        {
          _logger.LogInformation("{}: {}", _spaSettings.DevServerAddress, new string(chunk.Array));
        }
      }

      StdOut.OnReceivedLine += OnReceivedLine;
      StdErr.OnReceivedLine += OnReceivedLine;
      StdOut.OnReceivedChunk += OnReceivedChunk;
      StdErr.OnReceivedChunk += OnReceivedChunk;
    }
  }
}