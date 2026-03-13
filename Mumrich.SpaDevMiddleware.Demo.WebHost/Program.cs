using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.AspNetCore.Builder;

using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.Domain.Types;
using Mumrich.SpaDevMiddleware.Extensions;

namespace Mumrich.SpaDevMiddleware.Demo.WebHost;

internal class AppSettings : ISpaMiddlewareSettings
{
  public Dictionary<string, SpaSettings> SinglePageApps { get; set; } = [];

  public string BasePublicPath { get; set; } = Environment.CurrentDirectory;
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
            DevServerAddress = "http://127.0.0.1:3000/",
            SpaRootPath = "Apps/vue-app",
            NodePackageManager = NodePackageManager.VitePlus,
          }
        },
      },
      BasePublicPath = Directory.GetCurrentDirectory(),
    };

    builder.SetupSpaMiddleware(appSettings);

    var app = builder.Build();

    app.MapSinglePageApps(appSettings);

    app.Run();
  }
}
