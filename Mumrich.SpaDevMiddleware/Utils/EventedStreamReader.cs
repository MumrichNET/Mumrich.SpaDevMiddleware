using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mumrich.SpaDevMiddleware.Utils;

public class EventedStreamReader
{
  private readonly StringBuilder _linesBuffer;

  private readonly StreamReader _streamReader;

  public EventedStreamReader(StreamReader? streamReader)
  {
    _streamReader = streamReader ?? throw new ArgumentNullException(nameof(streamReader));
    _linesBuffer = new StringBuilder();
    // Observe exceptions from the read loop: treat a faulted reader as a closed stream
    // so that any pending WaitForMatch tasks complete with an EndOfStreamException.
    _ = Task.Run(Run).ContinueWith(
      t => OnClosed(),
      TaskContinuationOptions.OnlyOnFaulted
    );
  }

  public delegate void OnReceivedChunkHandler(ArraySegment<char> chunk);

  public delegate void OnReceivedLineHandler(string line);

  public delegate void OnStreamClosedHandler();

  public event OnReceivedChunkHandler? OnReceivedChunk;

  public event OnReceivedLineHandler? OnReceivedLine;

  public event OnStreamClosedHandler? OnStreamClosed;

  public Task<Match> WaitForMatch(Regex regex)
  {
    var tcs = new TaskCompletionSource<Match>();
    var completionLock = new object();

    OnReceivedLineHandler? onReceivedLineHandler = null;
    OnStreamClosedHandler? onStreamClosedHandler = null;

    void ResolveIfStillPending(Action applyResolution)
    {
      lock (completionLock)
      {
        if (tcs.Task.IsCompleted)
        {
          return;
        }

        OnReceivedLine -= onReceivedLineHandler;
        OnStreamClosed -= onStreamClosedHandler;
        applyResolution();
      }
    }

    onReceivedLineHandler = line =>
    {
      var match = regex.Match(line);

      if (match.Success)
      {
        ResolveIfStillPending(() => tcs.SetResult(match));
      }
    };

    OnReceivedLine += onReceivedLineHandler;

    onStreamClosedHandler = () =>
      ResolveIfStillPending(() => tcs.SetException(new EndOfStreamException()));

    OnStreamClosed += onStreamClosedHandler;

    return tcs.Task;
  }

  private void OnChunk(ArraySegment<char> chunk)
  {
    var dlg = OnReceivedChunk;
    dlg?.Invoke(chunk);
  }

  private void OnClosed()
  {
    var dlg = OnStreamClosed;
    dlg?.Invoke();
  }

  private void OnCompleteLine(string line)
  {
    var dlg = OnReceivedLine;
    dlg?.Invoke(line);
  }

  private async Task Run()
  {
    var buf = new char[8 * 1024];
    while (true)
    {
      var chunkLength = await _streamReader.ReadAsync(buf, 0, buf.Length);
      if (chunkLength == 0)
      {
        OnClosed();
        break;
      }

      OnChunk(new ArraySegment<char>(buf, 0, chunkLength));

      int startPos = 0;
      int lineBreakPos;

      // get all the newlines
      while (
        (lineBreakPos = Array.IndexOf(buf, '\n', startPos, chunkLength - startPos)) >= 0
        && startPos < chunkLength
      )
      {
        var length = lineBreakPos - startPos;
        _linesBuffer.Append(buf, startPos, length);
        OnCompleteLine(_linesBuffer.ToString());
        _linesBuffer.Clear();
        startPos = lineBreakPos + 1;
      }

      // get the rest
      if (lineBreakPos < 0 && startPos < chunkLength)
      {
        _linesBuffer.Append(buf, startPos, chunkLength - startPos);
      }
    }
  }
}
