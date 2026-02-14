using System.Collections.Generic;

using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware.Domain.Contracts
{
  /// <summary>
  /// Configuration contract for the spa-middleware.
  /// </summary>
  public interface ISpaMiddlewareSettings
  {
    /// <summary>
    /// All configurations of all single-page-apps, identified by their url-path.
    /// </summary>
    Dictionary<string, SpaSettings> SinglePageApps { get; set; }

    /// <summary>
    /// The base URL path where the root of the web-host resides.
    /// </summary>
    string BasePublicPath { get; set; }
  }
}
