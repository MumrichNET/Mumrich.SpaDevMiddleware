using System.Collections.Generic;
using System.Linq;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Mumrich.SpaDevMiddleware.Domain.Models;
using Mumrich.SpaDevMiddleware.MsBuild;
using Mumrich.SpaDevMiddleware.MsBuild.Extensions;

namespace Mumrich.SpaDevMiddleware.MSBuild
{
  public class SpaPublisherTask : MsBuildTaskBase
  {
    [Output]
    public TaskItem[] SpaPaths { get; set; }

    public override bool Execute()
    {
      var defaultAppSettings = TryReadDefaultAppSettingsFile();
      var envAppSettings = TryReadEnvAppSettingsFile();
      var spaSettings = new Dictionary<string, SpaSettings>();

      CollectSpaPaths(nameof(envAppSettings), spaSettings, envAppSettings);
      CollectSpaPaths(nameof(defaultAppSettings), spaSettings, defaultAppSettings);

      SpaPaths = spaSettings.Values.Select(v => new TaskItem(
        ConvertToMsBuildCompatiblePath(v.SpaRootPath),
        new Dictionary<string, string>()
        {
          { "NodeBuildScript", v.NodeBuildScript }
        })).ToArray();

      return true;
    }

    private void CollectSpaPaths(string prefix, Dictionary<string, SpaSettings> spaSettings, AppSettings miniSettings)
    {
      foreach (var kvp in miniSettings.SinglePageApps)
      {
        var route = kvp.Key;
        var spaDirectory = kvp.Value.SpaRootPath;

        if (spaSettings.TryAdd(route, kvp.Value))
        {
          LogImportantMessage($"{prefix} => '{route}': '{spaDirectory}'");
        }
      }
    }

    private void LogImportantMessage(string message)
    {
      Log.LogMessage(MessageImportance.High, message);
    }
  }
}