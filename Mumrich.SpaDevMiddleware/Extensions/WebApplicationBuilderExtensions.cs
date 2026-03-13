using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Mumrich.SpaDevMiddleware;
using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Domain.Types;
using Mumrich.SpaDevMiddleware.Helpers;

namespace Mumrich.SpaDevMiddleware.Extensions;

/// <summary>
/// Extension methods for <see cref="WebApplicationBuilder" />.
/// </summary>
public static class WebApplicationBuilderExtensions
{
  private const string SpaMiddlewarePolicyName = "spa-middleware-policy";
  private const string ConsecutiveFailuresThresholdKey = "ConsecutiveFailuresHealthPolicy.Threshold";
  private const string ConsecutiveFailuresThreshold = "3";
  private const string HealthCheckInterval = "00:00:10";
  private const string HealthCheckTimeout = "00:00:15";

  private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

  public static void SetupSpaMiddleware(
    this WebApplicationBuilder aBuilder,
    ISpaMiddlewareSettings aSpaMiddlewareSettings
  )
  {
    if (!aBuilder.Environment.CanSpaMiddlewareBeUsed())
    {
      return;
    }

    JsonObject origin = new();

    foreach ((string appPath, SpaSettings spaSettings) in aSpaMiddlewareSettings.SinglePageApps)
    {
      spaSettings.Validate();

      Guid guid = Guid.NewGuid();
      JsonObject current = spaSettings.Bundler switch
      {
        BundlerType.ViteJs => GetViteJsYarpConfig(appPath, guid, spaSettings),
        BundlerType.QuasarCli => GetQuasarYarpConfig(appPath, guid, spaSettings),
        BundlerType.Custom => SerializeToJsonObject(new { ReverseProxy = spaSettings.CustomYarpConfiguration }),
        _ => throw new NotImplementedException(),
      };

      MergeJsonObjects(origin, current);
    }

    string newConfig = origin.ToJsonString(_jsonOptions);

    Debug.WriteLine(newConfig);

    aBuilder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(newConfig)));

    IConfigurationSection reverseProxyConfig = aBuilder.Configuration.GetSection("ReverseProxy");

    aBuilder.Services.AddHttpClient();
    aBuilder.Services.AddSingleton(aSpaMiddlewareSettings);
    aBuilder.Services.AddHostedService<SpaDevelopmentService>();
    aBuilder.Services.AddReverseProxy().LoadFromConfig(reverseProxyConfig);
    aBuilder.Services.AddRequestTimeouts(aOptions =>
      aOptions.AddPolicy(SpaMiddlewarePolicyName, TimeSpan.FromSeconds(20))
    );
  }

  /// <summary>Recursively merges <paramref name="source"/> into <paramref name="target"/>, overwriting scalar values.</summary>
  private static void MergeJsonObjects(JsonObject target, JsonObject source)
  {
    foreach ((string key, JsonNode? value) in source)
    {
      if (target[key] is JsonObject targetChild && value is JsonObject sourceChild)
      {
        MergeJsonObjects(targetChild, sourceChild);
      }
      else
      {
        target[key] = value?.DeepClone();
      }
    }
  }

  private static JsonObject SerializeToJsonObject(object value)
  {
    return JsonSerializer.SerializeToNode(value, _jsonOptions)!.AsObject();
  }

  private static JsonObject GetQuasarYarpConfig(string aAppPath, Guid aAppId, SpaSettings aSpaSettings)
  {
    return GetYarpConfig(
      aAppPath,
      aSpaSettings,
      new Dictionary<string, string> { { $"SpaRoot-{aAppId}", "{**any}" } },
      aAppId
    );
  }

  private static JsonObject GetViteJsYarpConfig(string aAppPath, Guid aAppId, SpaSettings aSpaSettings)
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

  private static JsonObject GetYarpConfig(
    string aAppBasePath,
    SpaSettings aSpaSettings,
    Dictionary<string, string> aRouteMatches,
    Guid aAppId
  )
  {
    aAppBasePath = AppPathHelper.GetValidIntermediateAppPath(aAppBasePath);

    string clusterId = $"spa-cluster-{aAppId}";

    JsonObject rootConfig = SerializeToJsonObject(
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
      JsonObject yarpRouteConfig = GetYarpRoute(route, clusterId, fullAppPath, aSpaSettings);

      MergeJsonObjects(rootConfig, yarpRouteConfig);
    }

    return rootConfig;
  }

  private static JsonObject GetYarpRoute(string aRoute, string aClusterId, string aPath, SpaSettings aSpaSettings)
  {
    var proxyRouteConfig = new Dictionary<string, object>
    {
      { "ClusterId", aClusterId },
      { "Match", new { Path = aPath } },
    };

    if (aSpaSettings.AuthorizationPolicy != null)
    {
      proxyRouteConfig["AuthorizationPolicy"] = aSpaSettings.AuthorizationPolicy;
    }

    if (aSpaSettings.CorsPolicy != null)
    {
      proxyRouteConfig["CorsPolicy"] = aSpaSettings.CorsPolicy;
    }

    proxyRouteConfig["HealthCheck"] = new HealthCheck
    {
      Active = new ActiveHealthCheck
      {
        Enabled = aSpaSettings.HealthCheckEnabled.ToString().ToLowerInvariant(),
        Interval = HealthCheckInterval,
        Timeout = HealthCheckTimeout,
        Policy = "ConsecutiveFailures",
        Path = aPath,
      },
    };

    proxyRouteConfig["Metadata"] = new Dictionary<string, string>
    {
      { ConsecutiveFailuresThresholdKey, ConsecutiveFailuresThreshold },
    };

    return SerializeToJsonObject(
      new { ReverseProxy = new { Routes = new Dictionary<string, object> { { aRoute, proxyRouteConfig } } } }
    );
  }
}
