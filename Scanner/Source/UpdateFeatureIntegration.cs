using System.Threading.Tasks;

namespace WrongWayScanner
{
    public static class UpdateFeatureIntegration
    {
        private static VersionManager? _versionManager;
        private static UpdateChecker? _updateChecker;

        public static void Initialize(string? suiteVersion = null)
        {
            _versionManager ??= new VersionManager(suiteVersion);
            _updateChecker ??= new UpdateChecker(_versionManager);
        }

        public static VersionManager GetVersionManager()
        {
            Initialize();
            return _versionManager!;
        }

        public static UpdateChecker GetUpdateChecker()
        {
            Initialize();
            return _updateChecker!;
        }

        public static async Task<bool> CheckForUpdatesAsync()
        {
            Initialize();
            return await _updateChecker!.CheckAndPromptAsync();
        }

        public static VersionCheckResult? GetLastCheckResult()
        {
            Initialize();
            return _updateChecker!.GetLastCheckResult();
        }

        public static void NotifyUpdatesAvailable()
        {
            Initialize();
            _updateChecker!.NotifyUpdatesAvailable();
        }

        public static void ClearVersionCache()
        {
            Initialize();
            _updateChecker!.ClearCache();
        }

        public static async Task ShowVersionCheckDialogAsync(System.Action<string> onStatusChanged)
        {
            Initialize();
            var versionManager = GetVersionManager();
            var dialog = new VersionCheckDialog(versionManager, onStatusChanged);
            await dialog.ShowVersionCheckDialogAsync();
        }
    }
}
