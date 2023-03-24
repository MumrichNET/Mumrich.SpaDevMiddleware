using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Mumrich.SpaDevMiddleware.Domain.Models;

using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace Mumrich.SpaDevMiddleware.MsBuild
{
  public class AppSettings
  {
    public Dictionary<string, SpaSettings> SinglePageApps { get; set; } = new Dictionary<string, SpaSettings>();
  }

  public abstract class MsBuildTaskBase : MsBuildTask
  {
    private readonly JsonSerializerOptions _options;

    protected MsBuildTaskBase()
    {
      _options = new JsonSerializerOptions();
      _options.Converters.Add(new JsonStringEnumConverter());
    }

    public string AspNetCoreEnvironment { get; set; }
    public string CurrentDir { get; } = Directory.GetCurrentDirectory();
    protected bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    protected static string ConvertToMsBuildCompatiblePath(string spaRootPath)
    {
      var spaRootPathWithBackslashed = spaRootPath.Replace("/", "\\");
      return spaRootPathWithBackslashed.EndsWith("\\") ? spaRootPathWithBackslashed : $"{spaRootPathWithBackslashed}\\";
    }

    protected AppSettings TryReadAppSettings(string filePath)
    {
      if (filePath is null)
      {
        return new AppSettings();
      }

      var fileText = File.ReadAllText(filePath);
      return JsonSerializer.Deserialize<AppSettings>(fileText, _options);
    }

    protected AppSettings TryReadDefaultAppSettingsFile()
    {
      var defaultAppSettingsFile = Directory
        .GetFiles(CurrentDir, "appsettings.json")
        .FirstOrDefault();

      return TryReadAppSettings(defaultAppSettingsFile);
    }

    protected AppSettings TryReadEnvAppSettingsFile()
    {
      var envAppSettingsFile = Directory
        .GetFiles(CurrentDir, $"appsettings.{AspNetCoreEnvironment}.json")
        .FirstOrDefault();

      return TryReadAppSettings(envAppSettingsFile);
    }
  }
}