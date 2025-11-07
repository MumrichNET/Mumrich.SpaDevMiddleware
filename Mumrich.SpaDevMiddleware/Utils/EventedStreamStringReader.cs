using System;
using System.Text;

namespace Mumrich.SpaDevMiddleware.Utils;

internal class EventedStreamStringReader : IDisposable
{
  private readonly EventedStreamReader _eventedStreamReader;
  private readonly StringBuilder _stringBuilder = new();
  private bool _disposedValue;

  public EventedStreamStringReader(EventedStreamReader eventedStreamReader)
  {
    _eventedStreamReader =
      eventedStreamReader ?? throw new ArgumentNullException(nameof(eventedStreamReader));
    _eventedStreamReader.OnReceivedLine += OnReceivedLine;
  }

  public string ReadAsString() => _stringBuilder.ToString();

  private void OnReceivedLine(string line) => _stringBuilder.AppendLine(line);

  protected virtual void Dispose(bool disposing)
  {
    if (!_disposedValue)
    {
      if (disposing)
      {
        _eventedStreamReader.OnReceivedLine -= OnReceivedLine;
      }

      // free unmanaged resources (unmanaged objects) and override finalizer
      // set large fields to null
      _disposedValue = true;
    }
  }

  public void Dispose()
  {
    // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }
}
