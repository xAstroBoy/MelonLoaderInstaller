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
    /// Renames the target app's private data, external data and OBB dirs to a "<dir>_bak" sibling
    /// before the app is uninstalled. The uninstall removes the package by name, so the differently
    /// named _bak dirs survive. Returns true if anything was renamed.
    /// </summary>
    public static bool BackupAppData(string packageName, IPatchLogger? logger = null)
    {
        if (!RequestRoot(logger))
            return false;

        logger?.Log("Backing up app data via root (renaming to _bak)...");

        // Only rename if the source exists and a _bak isn't already present (don't clobber an
        // earlier backup from an interrupted run).
        bool any = false;
        any |= MoveToBak($"/data/data/{packageName}");
        any |= MoveToBak($"/sdcard/Android/data/{packageName}");
        any |= MoveToBak($"/sdcard/Android/obb/{packageName}");

        logger?.Log(any ? "App data renamed to _bak." : "No app data found to back up.");
        return any;
    }

    /// <summary>
    /// Restores the _bak dirs after the patched app is reinstalled, then deletes the _bak dirs.
    /// /data/data is merged into the freshly-created dir and re-owned to the new UID; the storage
    /// dirs (no per-app UID/SELinux) are simply renamed back.
    /// </summary>
    public static void RestoreAppData(string packageName, IPatchLogger? logger = null)
    {
        if (!IsAvailable)
            return;

        logger?.Log("Restoring app data from _bak via root...");

        // Private data: the reinstall created a fresh dir with a new UID; merge the backup into it,
        // re-own to that UID, restore SELinux context, then drop the _bak.
        Run(
            $"if [ -d /data/data/{packageName}_bak ]; then " +
            $"uid=$(stat -c %u /data/data/{packageName} 2>/dev/null); " +
            $"cp -a /data/data/{packageName}_bak/. /data/data/{packageName}/ 2>/dev/null; " +
            $"[ -n \"$uid\" ] && chown -R $uid:$uid /data/data/{packageName}; " +
            $"restorecon -R /data/data/{packageName} 2>/dev/null; " +
            $"rm -rf /data/data/{packageName}_bak; fi || true");

        // External storage / OBB: just move the backup back over the (re)created dir and drop _bak.
        RestoreStorageBak($"/sdcard/Android/data/{packageName}");
        RestoreStorageBak($"/sdcard/Android/obb/{packageName}");

        logger?.Log("App data restored and _bak removed.");
    }

    private static bool MoveToBak(string path)
    {
        (int code, _) = Run($"if [ -e '{path}' ] && [ ! -e '{path}_bak' ]; then mv '{path}' '{path}_bak'; fi; [ -e '{path}_bak' ]");
        return code == 0;
    }

    private static void RestoreStorageBak(string path)
    {
        Run($"if [ -e '{path}_bak' ]; then rm -rf '{path}'; mv '{path}_bak' '{path}'; fi || true");
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
