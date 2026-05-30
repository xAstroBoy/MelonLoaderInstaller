using MelonLoader.Installer.Core;

namespace MelonLoader.Installer.App.Utils;

/// <summary>
/// Optional root (su) helper. On a rooted device the on-device installer can fully back up and
/// restore an app's data across the uninstall/reinstall that patching requires (which is otherwise
/// "impossible" under scoped storage). Everything here is best-effort and no-ops when root is denied.
/// </summary>
public static class RootManager
{
    private static bool? _available;

    /// <summary>Root backups are stored here (root-owned, survives the target app's reinstall).</summary>
    private const string BackupRoot = "/data/local/tmp/lemon_bak";

    /// <summary>
    /// Asks for root once (triggers the superuser prompt) and caches whether it was granted.
    /// </summary>
    public static bool RequestRoot(IPatchLogger? logger = null)
    {
        if (_available.HasValue)
            return _available.Value;

#if ANDROID
        try
        {
            (int code, string output) = Run("id");
            _available = code == 0 && output.Contains("uid=0");
            logger?.Log(_available.Value ? "Root access granted." : "Root not available; continuing without it.");
        }
        catch (Exception ex)
        {
            logger?.Log("Root check failed; continuing without it.\n" + ex.Message);
            _available = false;
        }
#else
        _available = false;
#endif
        return _available.Value;
    }

    public static bool IsAvailable => _available ?? false;

    /// <summary>
    /// Backs up the target app's private data, external data and OBBs to a root-owned location
    /// (suffixed _bak) before it is uninstalled. Returns true if anything was backed up.
    /// </summary>
    public static bool BackupAppData(string packageName, IPatchLogger? logger = null)
    {
        if (!RequestRoot(logger))
            return false;

        logger?.Log("Backing up app data via root...");

        string bak = $"{BackupRoot}/{packageName}";
        Run($"rm -rf '{bak}'; mkdir -p '{bak}'");

        // App private data (most save data lives here), external data, and OBB assets.
        Run($"[ -d /data/data/{packageName} ] && cp -a /data/data/{packageName} '{bak}/data_bak' || true");
        Run($"[ -d /sdcard/Android/data/{packageName} ] && cp -a /sdcard/Android/data/{packageName} '{bak}/extdata_bak' || true");
        Run($"[ -d /sdcard/Android/obb/{packageName} ] && cp -a /sdcard/Android/obb/{packageName} '{bak}/obb_bak' || true");

        logger?.Log($"Root backup stored at {bak}");
        return true;
    }

    /// <summary>
    /// Restores a previous root backup after the patched app is reinstalled, fixing ownership/SELinux
    /// context so the freshly-installed app (new UID) can read its data.
    /// </summary>
    public static void RestoreAppData(string packageName, IPatchLogger? logger = null)
    {
        if (!IsAvailable)
            return;

        string bak = $"{BackupRoot}/{packageName}";
        (int existsCode, _) = Run($"[ -d '{bak}' ]");
        if (existsCode != 0)
            return;

        logger?.Log("Restoring app data via root...");

        // The reinstalled app gets a new UID; copy data back and re-own it to that UID.
        Run(
            $"if [ -d '{bak}/data_bak' ]; then " +
            $"uid=$(stat -c %u /data/data/{packageName}); " +
            $"cp -a '{bak}/data_bak/.' /data/data/{packageName}/ 2>/dev/null; " +
            $"chown -R $uid:$uid /data/data/{packageName}; " +
            $"restorecon -R /data/data/{packageName} 2>/dev/null; fi || true");

        Run($"if [ -d '{bak}/extdata_bak' ]; then mkdir -p /sdcard/Android/data/{packageName}; cp -a '{bak}/extdata_bak/.' /sdcard/Android/data/{packageName}/ 2>/dev/null; fi || true");
        Run($"if [ -d '{bak}/obb_bak' ]; then mkdir -p /sdcard/Android/obb/{packageName}; cp -a '{bak}/obb_bak/.' /sdcard/Android/obb/{packageName}/ 2>/dev/null; fi || true");

        logger?.Log("Root data restore complete.");
    }

#if ANDROID
    /// <summary>Runs a single shell command as root via `su -c`. Returns the exit code and combined output.</summary>
    public static (int exitCode, string output) Run(string command)
    {
        using System.Diagnostics.Process process = new();
        process.StartInfo.FileName = "su";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
#else
    public static (int exitCode, string output) Run(string command) => (1, string.Empty);
#endif
}
