using System;
using System.Linq;

namespace AnimeStudio
{
    internal static class UnityHumanoidBakeDiagnostics
    {
        public static string DescribeFailure(int exitCode, string logText, string unity, string logPath)
        {
            logText ??= string.Empty;
            var lines=logText.Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
            string reason;
            // Licensing clients can print signature/access-token warnings even on
            // successful starts. Only a terminal license rejection identifies this
            // failure; an unrelated script error must keep its own diagnosis.
            if(logText.Contains("No valid Unity Editor license found",StringComparison.OrdinalIgnoreCase) ||
               logText.Contains("No valid Unity license found",StringComparison.OrdinalIgnoreCase))
            {
                reason="Unity Editor has no valid license (missing, expired, or inactive), so it exited before the Humanoid bake ran. " +
                    "Open Unity Hub > Settings > Licenses, sign in again and renew/activate a license for this editor. " +
                    "Open an empty project with this editor once, then retry the export. " +
                    "Cached clips may still export; uncached clips need a licensed editor. Keep the HumanoidCache folder.";
            }
            else if(logText.Contains("Scripts have compiler errors",StringComparison.OrdinalIgnoreCase) ||
                    lines.Any(l=>l.Contains("error CS",StringComparison.Ordinal)))
            {
                reason="The Unity Humanoid bake script could not compile. " +
                    lines.FirstOrDefault(l=>l.Contains("error CS",StringComparison.Ordinal));
            }
            else if(logText.Contains("another Unity instance",StringComparison.OrdinalIgnoreCase) ||
                    logText.Contains("Multiple Unity instances cannot open the same project",StringComparison.OrdinalIgnoreCase))
            {
                reason="The Humanoid cache project is already open in another Unity instance. Close that project and retry the export.";
            }
            else
            {
                var detail=lines.FirstOrDefault(l=>l.Contains("Exception:",StringComparison.Ordinal) ||
                    l.Contains("Exception: ",StringComparison.Ordinal));
                reason=detail ?? "Unity did not produce a confirmed Humanoid bake. Inspect the Unity log for the startup or bake error.";
            }
            return $"Unity Humanoid bake failed (exit {exitCode}). {reason}\nUnity: {unity}\nLog: {logPath}";
        }
    }
}
