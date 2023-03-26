using System.Collections.Generic;

using Mumrich.SpaDevMiddleware.Domain.Models;

namespace Mumrich.SpaDevMiddleware.Domain.Contracts
{
  public interface ISpaDevServerSettings
  {
    /// <summary>
    /// All configurations of all single-page-apps, identified by their url-path.
    /// </summary>
    Dictionary<string, SpaSettings> SinglePageApps { get; set; }

    /// <summary>
    /// The root-path where the single-page-apps are contained.
    /// </summary>
    string SpaRootPath { get; set; }
  }
}