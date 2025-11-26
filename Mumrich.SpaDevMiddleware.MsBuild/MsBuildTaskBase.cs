using System.Runtime.InteropServices;

using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace Mumrich.SpaDevMiddleware.MsBuild;

public abstract class MsBuildTaskBase : MsBuildTask
{
  public string CurrentDir { get; } = Directory.GetCurrentDirectory();
  protected bool IsWindows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  protected static string ConvertToMsBuildCompatiblePath(string spaRootPath)
  {
    var spaRootPathWithBackslashed = spaRootPath.Replace("/", "\\");
    return spaRootPathWithBackslashed.EndsWith("\\")
      ? spaRootPathWithBackslashed
      : $"{spaRootPathWithBackslashed}\\";
  }
}
