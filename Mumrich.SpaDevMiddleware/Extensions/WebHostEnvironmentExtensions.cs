using System.Collections.Generic;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Mumrich.SpaDevMiddleware.Extensions;

public static class WebHostEnvironmentExtensions
{
  private static readonly HashSet<string> PROHIBITED_HOST_ENVIRONMENTS_FOR_SPA_MAPPING =
    new(System.StringComparer.OrdinalIgnoreCase) { "NSWAG", "CAKE" };

  public static bool CanSpaMiddlewareBeUsed(this IWebHostEnvironment aWebHostEnvironment)
  {
    return aWebHostEnvironment.IsDevelopment();
  }

  public static bool CanSinglePageAppBeMapped(this IWebHostEnvironment aWebHostEnvironment)
  {
    return !PROHIBITED_HOST_ENVIRONMENTS_FOR_SPA_MAPPING.Contains(aWebHostEnvironment.EnvironmentName);
  }
}