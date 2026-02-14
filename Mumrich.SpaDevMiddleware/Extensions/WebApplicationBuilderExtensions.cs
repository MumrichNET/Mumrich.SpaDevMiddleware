using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Mumrich.SpaDevMiddleware;
using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Domain.Types;
using Mumrich.SpaDevMiddleware.Helpers;

using Newtonsoft.Json.Linq;

namespace Mumrich.SpaDevMiddleware.Extensions;

/// <summary>
/// Extension methods for <see cref="WebApplicationBuilder" />.
/// </summary>
public static class WebApplicationBuilderExtensions
{
  public static void SetupSpaMiddleware(
    this WebApplicationBuilder aBuilder,
    ISpaMiddlewareSettings aSpaMiddlewareSettings
  )
  {
    if (!aBuilder.Environment.CanSpaMiddlewareBeUsed())
    {
      return;
    }

    JObject origin = [];

    foreach ((string appPath, SpaSettings spaSettings) in aSpaMiddlewareSettings.SinglePageApps)
    {
      Guid guid = Guid.NewGuid();
      JObject current = spaSettings.Bundler switch
      {
        BundlerType.ViteJs => GetViteJsYarpConfig(appPath, guid, spaSettings),
        BundlerType.QuasarCli => GetQuasarYarpConfig(appPath, guid, spaSettings),
        BundlerType.Custom => JObject.FromObject(new { ReverseProxy = spaSettings.CustomYarpConfiguration }),
        _ => throw new NotImplementedException(),
      };

      origin.Merge(current);
    }

    string newConfig = origin.ToString();

    Console.WriteLine(newConfig);

    aBuilder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(newConfig)));

    IConfigurationSection reverseProxyConfig = aBuilder.Configuration.GetSection("ReverseProxy");

    aBuilder.Services.AddHttpClient();
    aBuilder.Services.AddSingleton(aSpaMiddlewareSettings);
    aBuilder.Services.AddHostedService<SpaDevelopmentService>();
    aBuilder.Services.AddReverseProxy().LoadFromConfig(reverseProxyConfig);
    aBuilder.Services.AddRequestTimeouts(aOptions =>
      aOptions.AddPolicy("spa-middleware-policy", TimeSpan.FromSeconds(20))
    );
  }

  private static JObject GetQuasarYarpConfig(string aAppPath, Guid aAppId, SpaSettings aSpaSettings)
  {
    return GetYarpConfig(
      aAppPath,
      aSpaSettings,
      new Dictionary<string, string> { { $"SpaRoot-{aAppId}", "{**any}" } },
      aAppId
    );
  }

  private static JObject GetViteJsYarpConfig(string aAppPath, Guid aAppId, SpaSettings aSpaSettings)
  {
    return GetYarpConfig(
      aAppPath,
      aSpaSettings,
      new Dictionary<string, string>
      {
        { $"SpaRoot-{aAppId}", $"{{filename:regex({aSpaSettings.SpaRootExpression})?}}" },
        { $"SpaAssets-{aAppId}", $"{{name:regex({aSpaSettings.SpaAssetsExpression})}}/{{**any}}" },
      },
      aAppId
    );
  }

  private static JObject GetYarpConfig(
    string aAppBasePath,
    SpaSettings aSpaSettings,
    Dictionary<string, string> aRouteMatches,
    Guid aAppId
  )
  {
    aAppBasePath = AppPathHelper.GetValidIntermediateAppPath(aAppBasePath);

    string clusterId = $"spa-cluster-{aAppId}";

    JObject rootConfig = JObject.FromObject(
      new
      {
        ReverseProxy = new
        {
          Clusters = new Dictionary<string, object>
          {
            {
              clusterId,
              new
              {
                Destinations = new Dictionary<string, object>
                {
                  { $"spa-cluster-destination-{aAppId}", new { Address = aSpaSettings.DevServerAddress } },
                },
              }
            },
          },
        },
      }
    );

    foreach ((string route, string path) in aRouteMatches)
    {
      string fullAppPath = $"{aAppBasePath}/{path}".Replace("//", "/");
      JObject yarpRouteConfig = GetYarpRoute(route, clusterId, fullAppPath, aSpaSettings);

      rootConfig.Merge(yarpRouteConfig);
    }

    return rootConfig;
  }

  private static JObject GetYarpRoute(string aRoute, string aClusterId, string aPath, SpaSettings aSpaSettings)
  {
    ExpandoObject proxyRouteConfig = new();

    proxyRouteConfig.TryAdd("ClusterId", aClusterId);
    proxyRouteConfig.TryAdd("Match", new { Path = aPath });

    if (aSpaSettings.AuthorizationPolicy != null)
    {
      proxyRouteConfig.TryAdd("AuthorizationPolicy", aSpaSettings.AuthorizationPolicy);
    }

    if (aSpaSettings.CorsPolicy != null)
    {
      proxyRouteConfig.TryAdd("CorsPolicy", aSpaSettings.CorsPolicy);
    }

    proxyRouteConfig.TryAdd(
      "HealthCheck",
      new HealthCheck
      {
        Active = new ActiveHealthCheck
        {
          Enabled = aSpaSettings.HealthCheckEnabled.ToString().ToLowerInvariant(),
          Interval = "00:00:10",
          Timeout = "00:00:15",
          Policy = "ConsecutiveFailures",
          Path = aPath,
        },
      }
    );

    proxyRouteConfig.TryAdd(
      "Metadata",
      new Dictionary<string, string> { { "ConsecutiveFailuresHealthPolicy.Threshold", "3" } }
    );

    return JObject.FromObject(
      new { ReverseProxy = new { Routes = new Dictionary<string, ExpandoObject> { { aRoute, proxyRouteConfig } } } }
    );
  }
}
