namespace MelonLoader.Installer.App.Utils;

public static class AndroidPermissionHandler
{
    public static bool HaveRequired()
    {
        if (HasAccessToAllFiles() && CanInstallUnknownSources())
            return true;

        // On a rooted device we can grant the required permissions ourselves instead of bouncing
        // the user through the system settings screens.
        TryGrantViaRoot();

        return HasAccessToAllFiles() && CanInstallUnknownSources();
    }

    /// <summary>
    /// Grants the permissions the installer needs via root (appops / pm grant). No-op without root.
    /// Returns true if root was available and the commands were issued.
    /// </summary>
    public static bool TryGrantViaRoot()
    {
#if ANDROID
        if (!RootManager.RequestRoot())
            return false;

        string pkg = Platform.CurrentActivity!.PackageName!;
        RootManager.Run($"appops set {pkg} MANAGE_EXTERNAL_STORAGE allow");
        RootManager.Run($"appops set {pkg} REQUEST_INSTALL_PACKAGES allow");
        RootManager.Run($"pm grant {pkg} android.permission.READ_EXTERNAL_STORAGE 2>/dev/null; pm grant {pkg} android.permission.WRITE_EXTERNAL_STORAGE 2>/dev/null");
        return true;
#else
        return false;
#endif
    }

    public static bool HasAccessToAllFiles()
    {
#if ANDROID30_0_OR_GREATER
#pragma warning disable CA1416 // Validate platform compatibility; I'm clearly already checking it
        return Android.OS.Environment.IsExternalStorageManager;
#pragma warning restore CA1416
#else
        return true;
#endif
    }

    public static bool CanInstallUnknownSources()
    {
#if ANDROID
        return Platform.CurrentActivity!.PackageManager!.CanRequestPackageInstalls();
#else
        return true;
#endif
    }

    public static void TryGetAccessToAllFiles()
    {
#if ANDROID30_0_OR_GREATER
        if (TryGrantViaRoot() && HasAccessToAllFiles())
            return;

#pragma warning disable CA1416 // Validate platform compatibility; I'm clearly already checking it
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + Platform.CurrentActivity!.PackageName));
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.CurrentActivity!.StartActivity(intent);
#pragma warning restore CA1416
#endif
    }

    public static void TryGetInstallUnknownSources()
    {
#if ANDROID
        if (TryGrantViaRoot() && CanInstallUnknownSources())
            return;

        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + Platform.CurrentActivity!.PackageName));
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.CurrentActivity!.StartActivity(intent);
#endif
    }
}