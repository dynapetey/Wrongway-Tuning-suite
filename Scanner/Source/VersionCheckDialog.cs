using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace WrongWayScanner
{
    public sealed class VersionCheckDialog
    {
        private readonly VersionManager _versionManager;
        private readonly Action<string> _onStatusChanged;

        public VersionCheckDialog(VersionManager versionManager, Action<string> onStatusChanged)
        {
            _versionManager = versionManager;
            _onStatusChanged = onStatusChanged;
        }

        public async Task ShowVersionCheckDialogAsync()
        {
            _onStatusChanged("Checking for updates...");

            try
            {
                var result = await _versionManager.CheckForUpdatesAsync();
                DisplayVersionInfo(result);
            }
            catch (Exception ex)
            {
                _onStatusChanged($"Version check failed: {ex.Message}");
            }
        }

        private void DisplayVersionInfo(VersionCheckResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔════════════════════════════════════════════╗");
            sb.AppendLine("║      WrongWay Suite - Version Check        ║");
            sb.AppendLine("╚════════════════════════════════════════════╝");
            sb.AppendLine();
            sb.AppendLine($"Suite Version: {result.SuiteVersion}");
            sb.AppendLine($"Last Checked: {result.LastChecked:g}");
            sb.AppendLine();

            if (result.HasUpdates)
            {
                sb.AppendLine($"🔄 {result.UpdateCount} update(s) available:");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("✓ All components are up to date");
                sb.AppendLine();
            }

            foreach (var component in result.Components)
            {
                sb.Append("  • ");
                if (component.UpdateAvailable)
                {
                    sb.AppendLine($"{component.Name}");
                    sb.AppendLine($"    Current: {component.CurrentVersion}");
                    sb.AppendLine($"    Latest:  {component.LatestVersion}");
                    if (!string.IsNullOrEmpty(component.DownloadUrl))
                    {
                        sb.AppendLine($"    Download: {component.DownloadUrl}");
                    }
                }
                else
                {
                    sb.AppendLine($"{component.Name} {component.CurrentVersion}");
                }

                if (!string.IsNullOrEmpty(component.ReleaseNotes))
                {
                    sb.AppendLine($"    Notes: {component.ReleaseNotes.Substring(0, Math.Min(80, component.ReleaseNotes.Length))}...");
                }

                sb.AppendLine();
            }

            sb.AppendLine("────────────────────────────────────────────");
            sb.AppendLine("Visit: https://github.com/dynapetey/Wrongway-Tuning-suite/releases");
            sb.AppendLine();

            _onStatusChanged(sb.ToString());
        }

        public string FormatComponentList(VersionCheckResult result)
        {
            var sb = new StringBuilder();

            foreach (var component in result.Components)
            {
                sb.AppendLine(component.ToString());
            }

            return sb.ToString();
        }
    }

    public sealed class UpdateChecker
    {
        private readonly VersionManager _versionManager;
        private VersionCheckResult? _lastResult;

        public UpdateChecker(VersionManager versionManager)
        {
            _versionManager = versionManager;
        }

        public async Task<bool> CheckAndPromptAsync()
        {
            _lastResult = await _versionManager.CheckForUpdatesAsync();
            return _lastResult.HasUpdates;
        }

        public VersionCheckResult? GetLastCheckResult() => _lastResult;

        public void NotifyUpdatesAvailable()
        {
            if (_lastResult?.HasUpdates == true)
            {
                System.Console.WriteLine(
                    $"\n⚠️  {_lastResult.UpdateCount} update(s) available. " +
                    $"Visit https://github.com/dynapetey/Wrongway-Tuning-suite/releases\n"
                );
            }
        }

        public void ClearCache() => _versionManager.ClearCache();
    }
}
