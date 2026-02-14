using System.Linq;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Mumrich.SpaDevMiddleware.Extensions;

public static class WebHostEnvironmentExtensions
{
  private static readonly string[] PROHIBITED_HOST_ENVIRONMENTS_FOR_SPA_MAPPING = ["NSWAG", "CAKE"];

  public static bool CanSpaMiddlewareBeUsed(this IWebHostEnvironment aWebHostEnvironment)
  {
    return aWebHostEnvironment.IsDevelopment();
  }

  public static bool CanSinglePageAppBeMapped(this IWebHostEnvironment aWebHostEnvironment)
  {
    return !PROHIBITED_HOST_ENVIRONMENTS_FOR_SPA_MAPPING.Any(aHostingEnvironment =>
      aHostingEnvironment == aWebHostEnvironment.EnvironmentName
    );
  }
}