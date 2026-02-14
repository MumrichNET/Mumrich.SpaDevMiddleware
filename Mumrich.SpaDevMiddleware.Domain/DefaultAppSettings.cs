using System.Collections.Generic;
using System.IO;

using Mumrich.SpaDevMiddleware.Domain.Contracts;
using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware.Domain
{
  public class DefaultAppSettings : ISpaMiddlewareSettings
  {
    public Dictionary<string, SpaSettings> SinglePageApps { get; set; } = new Dictionary<string, SpaSettings>();
    public string BasePublicPath { get; set; } = Directory.GetCurrentDirectory();
  }
}