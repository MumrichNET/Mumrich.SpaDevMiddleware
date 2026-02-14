using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

namespace Mumrich.SpaDevMiddleware.SignalR;

public interface ISpaDevServerLogHub
{
  Task ReceiveLogEntry(string aSpaDevServerName, string aMessage, bool aIsError = false);
}

public class SpaDevServerLogHub : Hub<ISpaDevServerLogHub>
{
}