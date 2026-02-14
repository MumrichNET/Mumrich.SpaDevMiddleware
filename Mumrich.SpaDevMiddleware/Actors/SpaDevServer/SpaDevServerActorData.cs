using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware.Actors.SpaDevServer;

public class SpaDevServerActorData : ISpaDevServerActorData
{
  public SpaSettings? SpaSettings { get; init; }
  public string? BasePublicPath { get; init; }
}
