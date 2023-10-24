using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Domain.Types;
using Mumrich.SpaDevMiddleware.Extensions;

namespace Mumrich.SpaDevMiddleware.Demo.WebHost;

internal class AppSettings : ISpaDevServerSettings
{
  public Dictionary<string, SpaSettings> SinglePageApps { get; set; } = new();
  public string SpaRootPath { get; set; }
  public bool UseParentObserverServiceOnWindows { get; set; }
}

public static class Program
{
  public static void Main(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);
    var appSettings = new AppSettings
    {
      SinglePageApps = new Dictionary<string, SpaSettings>()
      {
        {
          "/",
          new SpaSettings()
          {
            DevServerAddress = "http://localhost:3000/",
            SpaRootPath = "Apps/vue-demo-app",
            NodePackageManager = NodePackageManager.Npm
          }
        }
      },
      SpaRootPath = Directory.GetCurrentDirectory(),
      UseParentObserverServiceOnWindows = false
    };

    builder.RegisterSinglePageAppDevMiddleware(appSettings);

    var app = builder.Build();

    app.MapGet("/hello", () => "Hello World!");

    app.Run();
  }
}