using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Microsoft.Build.Framework;

using Mumrich.SpaDevMiddleware.Domain.Types;

namespace Mumrich.SpaDevMiddleware.MsBuild
{
  public class NodePackageInstallTask : MsBuildTaskBase
  {
    public override bool Execute()
    {
      var envAppSettings = TryReadEnvAppSettingsFile();
      var defaultAppSettings = TryReadDefaultAppSettingsFile();
      var spaSettingsList = envAppSettings?.SinglePageApps.Union(defaultAppSettings?.SinglePageApps);

      if (spaSettingsList.Any())
      {
        foreach (var spaSettings in spaSettingsList)
        {
          var fullPath = ConvertToMsBuildCompatiblePath(Path.Combine(CurrentDir, spaSettings.Value.SpaRootPath));

          Log.LogMessage(MessageImportance.High, fullPath);

          var p = Process.Start(new ProcessStartInfo(GetPackageManagerExeName(spaSettings.Value.NodePackageManager))
          {
            Arguments = GetPackageManagerInstallCommand(spaSettings.Value.NodePackageManager),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetFullPath(Path.Combine(CurrentDir, spaSettings.Value.SpaRootPath))
          });

          Log.LogMessagesFromStream(p.StandardOutput, MessageImportance.High);
          Log.LogMessagesFromStream(p.StandardError, MessageImportance.High);

          p.WaitForExit();

          if (p.ExitCode != 0)
          {
            return false;
          }
        }
      }

      return true;
    }

    private string GetPackageManagerExeName(NodePackageManager packageManager)
    {
      if (IsWindows)
      {
        return "cmd";
      }

      switch (packageManager)
      {
        case NodePackageManager.Npm:
          return "npm";

        case NodePackageManager.Yarn:
          return "yarn";

        case NodePackageManager.Npx:
        case NodePackageManager.Pnpm:
        default:
          throw new NotImplementedException();
      }
    }

    private string GetPackageManagerInstallCommand(NodePackageManager packageManager)
    {
      switch (packageManager)
      {
        case NodePackageManager.Npm:
          return IsWindows ? "/c npm ci" : "ci";

        case NodePackageManager.Yarn:
          return IsWindows ? "/c yarn install" : "install";

        case NodePackageManager.Npx:
        case NodePackageManager.Pnpm:
        default:
          throw new NotImplementedException();
      }
    }
  }
}