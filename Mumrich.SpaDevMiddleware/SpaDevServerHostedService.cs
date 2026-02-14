using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Akka.Actor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Mumrich.AkkaExt;
using Mumrich.SpaDevMiddleware.Actors.SpaDevServer;
using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware;

public class SpaDevelopmentService : AkkaServiceBase, IHostedService
{
  private readonly IHostApplicationLifetime _appLifetime;
  private readonly Dictionary<string, IActorRef> _processRunners = [];
  private readonly ISpaMiddlewareSettings _spaMiddlewareSettings;

  public SpaDevelopmentService(IServiceProvider aServiceProvider)
    : base("spa-development-system", aServiceProvider)
  {
    _appLifetime = aServiceProvider.GetRequiredService<IHostApplicationLifetime>();
    _spaMiddlewareSettings = aServiceProvider.GetRequiredService<ISpaMiddlewareSettings>();
  }

  public Task StartAsync(CancellationToken aCancellationToken)
  {
    aCancellationToken.ThrowIfCancellationRequested();

    foreach ((string basePublicPath, SpaSettings spaSettings) in _spaMiddlewareSettings.SinglePageApps)
    {
      string fullBasePublicPath = string.IsNullOrWhiteSpace(_spaMiddlewareSettings.BasePublicPath)
        ? basePublicPath
        : $"{_spaMiddlewareSettings.BasePublicPath}/{basePublicPath}".Replace("//", "/");

      _processRunners.Add(
        fullBasePublicPath,
        AkkaSystem.ActorOf(DependencyInjectionResolver.Props<SpaDevServerActor>(fullBasePublicPath, spaSettings))
      );
    }

    RegisterApplicationShutdownIfAkkaSystemTerminates(_appLifetime, aCancellationToken);

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken aCancellationToken)
  {
    return GracefullyShutdownAkkaSystemAsync();
  }
}