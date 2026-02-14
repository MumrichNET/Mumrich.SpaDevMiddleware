using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware.Actors.SpaDevServer;

public interface ISpaDevServerActorData
{
  string? BasePublicPath { get; init; }
  SpaSettings? SpaSettings { get; init; }
}
