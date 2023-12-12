using System.Collections.Generic;
using System.Linq;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Mumrich.SpaDevMiddleware.MsBuild
{
  public class SpaAppSettingsProviderTask : MsBuildTaskBase
  {
    [Required]
    public ITaskItem[] SpaApps { get; set; }

    [Output]
    public TaskItem[] SpaRoots { get; set; }

    public override bool Execute()
    {
      foreach (ITaskItem item in SpaApps)
      {
        Log.LogMessage(MessageImportance.High, $"*** Item: {item.ItemSpec}");
      }

      SpaRoots = SpaApps
        .Select(x =>
          new TaskItem(
            ConvertToMsBuildCompatiblePath(x.ItemSpec),
            new Dictionary<string, string>
            {

            }))
        .ToArray();

      return true;
    }
  }
}