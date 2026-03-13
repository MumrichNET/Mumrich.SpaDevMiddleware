using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Helpers;

namespace Mumrich.SpaDevMiddleware.Extensions;

/// <summary>
/// Extension methods for <see cref="WebApplication" />.
/// </summary>
public static class WebApplicationExtensions
{
  public static void MapSinglePageApp(
    this WebApplication aWebApplication,
    string aAppPath,
    SpaSettings aSpaSettings
  )
  {
    ILogger<WebApplication> logger = aWebApplication.Services.GetRequiredService<ILogger<WebApplication>>();
    string clientAppRoot = Path.GetFullPath(
      Path.Combine(
        aWebApplication.Environment.ContentRootPath,
        aSpaSettings.SpaRootPath ?? ".",
        aSpaSettings.NodeBuildOutputPath
      )
    );

    Directory.CreateDirectory(clientAppRoot);

    aWebApplication.UseStaticFiles(
      new StaticFileOptions
      {
        FileProvider = new PhysicalFileProvider(clientAppRoot),
        RequestPath = aAppPath == "/" ? string.Empty : aAppPath,
      }
    );

    string clientAppIndex = Path.GetFullPath(Path.Combine(clientAppRoot, aSpaSettings.AppIndexFileName));

    string appPath = AppPathHelper.GetValidIntermediateAppPath(aAppPath);
    logger.LogInformation(
      "*** Mapping Single Page App at '{AppPath}' with index file '{ClientAppIndex}'",
      appPath,
      clientAppIndex
    );

    aWebApplication.MapGet(appPath, async aContext => await aContext.Response.SendFileAsync(clientAppIndex));
  }

  public static void MapSinglePageApps(
    this WebApplication aWebApplication,
    ISpaMiddlewareSettings aSpaMiddlewareSettings
  )
  {
    if (aWebApplication.Environment.CanSpaMiddlewareBeUsed())
    {
      foreach ((string path, SpaSettings spaSettings) in aSpaMiddlewareSettings.SinglePageApps)
      {
        UseWaitForDevServerMiddleware(aWebApplication, path, spaSettings);
      }

      aWebApplication.MapReverseProxy();
    }
    else if (aWebApplication.Environment.CanSinglePageAppBeMapped())
    {
      foreach ((string appPath, SpaSettings spaSettings) in aSpaMiddlewareSettings.SinglePageApps)
      {
        aWebApplication.MapSinglePageApp(appPath, spaSettings);
      }
    }
  }

  private static void UseWaitForDevServerMiddleware(
    WebApplication aWebApplication,
    string aAppPath,
    SpaSettings aSpaSettings
  )
  {
    ILogger<WebApplication> logger = aWebApplication.Services.GetRequiredService<ILogger<WebApplication>>();
    IHttpClientFactory httpClientFactory = aWebApplication.Services.GetRequiredService<IHttpClientFactory>();

    aWebApplication.Use(
      async (aHttpContext, aNext) =>
      {
        string? requestPath = aHttpContext.Request.Path.Value;

        logger.LogInformation("*** {AppPath} Request Path: '{RequestPath}'", aAppPath, requestPath);

        using HttpClient httpClient = httpClientFactory.CreateClient();
        TimeSpan maxWaitTime = TimeSpan.FromSeconds(aSpaSettings.DevServerStartupTimeoutSeconds);
        TimeSpan waitInterval = TimeSpan.FromSeconds(aSpaSettings.DevServerStartupRetryIntervalSeconds);
        DateTime startTime = DateTime.UtcNow;

        httpClient.Timeout = TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
          try
          {
            HttpResponseMessage response = await httpClient.GetAsync(aSpaSettings.DevServerAddress);

            if (response.IsSuccessStatusCode)
            {
              logger.LogInformation(
                "Dev-server at {DevServerAddress} is up ({HttpCode}), continue...",
                aSpaSettings.DevServerAddress,
                response.StatusCode
              );
              break;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
              logger.LogWarning(
                "Dev-server at {DevServerAddress} responded with {HttpCode} ({HttpCodeStatus}). Treating dev-server as available and continuing.",
                aSpaSettings.DevServerAddress,
                (int)response.StatusCode,
                response.StatusCode.ToString()
              );
              break;
            }

            logger.LogInformation(
              "*** Checking dev-server at {DevServerAddress}, got HttpCode {HttpCode} ({HttpCodeStatus})",
              aSpaSettings.DevServerAddress,
              (int)response.StatusCode,
              response.StatusCode.ToString()
            );
          }
          catch (Exception aException)
          {
            logger.LogWarning(
              aException,
              "{DevServerAddress} is still down, wait and retry: {Exception}",
              aSpaSettings.DevServerAddress,
              aException.ToString()
            );
          }

          await Task.Delay(waitInterval);
        }

        await aNext();
      }
    );
  }
}
