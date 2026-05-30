using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace MelonLoader.Installer.App.Utils;

public static class UnityVersionFinder
{
    // Canonical Unity engine version token: <major>.<minor>.<patch><type><build>
    // (type: a=alpha b=beta c=china f=final p=patch x=experimental).
    private static readonly Regex UnityVersionToken = new(@"\d+\.\d+\.\d+[abcfpx]\d+", RegexOptions.Compiled);

    public static async Task<UnityVersion> ParseUnityVersion(UnityApplicationFinder.Data data, string tempPath)
    {
#if ANDROID
        if (data.Source == UnityApplicationFinder.Source.PackageManager)
            return await AndroidParseUnityVersion(data);
#endif
        if (data.Source == UnityApplicationFinder.Source.File)
            return await FileParseUnityVersion(data, data.APKPaths.First());

        if (data.Source == UnityApplicationFinder.Source.ADB)
            return await ADBParseUnityVersion(data, tempPath);

        return UnityVersion.MinVersion;
    }

#if ANDROID
    private static async Task<UnityVersion> AndroidParseUnityVersion(UnityApplicationFinder.Data data)
    {
        Android.Content.PM.PackageManager pm = Platform.CurrentActivity!.PackageManager!;
        Android.Content.PM.ApplicationInfo packageInfo = pm.GetApplicationInfo(data.PackageName, Android.Content.PM.PackageInfoFlags.MetaData);
        Android.Content.Res.AssetManager assetManager = pm.GetResourcesForApplication(packageInfo)!.Assets!;

        Stream? ggmStream = null;
        try
        {
            ggmStream = assetManager.Open("bin/Data/globalgamemanagers");
        }
        catch { }

        Stream? dataStream = null;
        try
        {
            dataStream = assetManager.Open("bin/Data/data.unity3d");
        }
        catch { }

        return await GenericParseUnityVersion(data, ggmStream, dataStream);
    }
#endif

    private static async Task<UnityVersion> FileParseUnityVersion(UnityApplicationFinder.Data data, string apkPath)
    {
        using FileStream apkStream = new(apkPath, FileMode.Open);
        using ZipArchive archive = new(apkStream, ZipArchiveMode.Read);

        Stream? ggmStream = archive.GetEntry("assets/bin/Data/globalgamemanagers")?.Open();
        Stream? dataStream = archive.GetEntry("assets/bin/Data/data.unity3d")?.Open();

        if (ggmStream == null && dataStream == null)
            return UnityVersion.MinVersion;

        return await GenericParseUnityVersion(data, ggmStream, dataStream);
    }

    // TODO: this is incredibly wasteful, should see if I can do this after copying apks then just read those instead of pulling and deleting
    private static async Task<UnityVersion> ADBParseUnityVersion(UnityApplicationFinder.Data data, string tempPath)
    {
        foreach (string deviceApkPath in data.APKPaths)
        {
            // handle splits, but skip configs (libraries for the most part)
            if (Path.GetFileName(deviceApkPath).StartsWith("split_config"))
                continue;

            string destinationPath = Path.Combine(tempPath, Path.GetFileName(deviceApkPath));

            await ADBManager.PullFileToPath(deviceApkPath, destinationPath);

            UnityVersion res = await FileParseUnityVersion(data, destinationPath);

            File.Delete(destinationPath);

            if (res != UnityVersion.MinVersion)
                return res;
        }

        throw new Exception("Failed to parse Unity version.");
    }

    private static async Task<UnityVersion> GenericParseUnityVersion(UnityApplicationFinder.Data data, Stream? globalgamemanagers, Stream? dataUnity3d)
    {
        AssetsManager uAssetsManager = new();

        // Try to read directly from file
        try
        {
            Stream stream = globalgamemanagers ?? throw new Exception();
            using MemoryStream memoryStream = new();
            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            }

            memoryStream.Seek(0, SeekOrigin.Begin);

            stream.Dispose();

            AssetsFileInstance instance = uAssetsManager.LoadAssetsFile(memoryStream, "/bin/Data/globalgamemanagers", true);
            return TryParseUnityVersion(instance.file.Metadata.UnityVersion, data);
        }
        catch { }

        // If failed before, try to get the data from data.unity3d
        try
        {
            Stream stream = dataUnity3d ?? throw new Exception("data.unity3d does not exist");

            using MemoryStream memoryStream = new();
            byte[] buffer = new byte[4096];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            }

            memoryStream.Seek(0, SeekOrigin.Begin);

            stream.Dispose();

            BundleFileInstance bundle = uAssetsManager.LoadBundleFile(memoryStream, "/bin/Data/data.unity3d");

            // Don't use LoadAssetsFileFromBundle: its IsAssetsFile() heuristic returns a false
            // negative for SerializedFile format >= 0x16 (Unity 2017+/2020+/2022+) and yields null.
            // Read the serialized file's bytes out of the bundle and load them standalone instead.
            AssetsFileInstance? instance = LoadSerializedFileFromBundle(uAssetsManager, bundle, "globalgamemanagers")
                ?? throw new Exception("globalgamemanagers not found in data.unity3d");
            return TryParseUnityVersion(instance.file.Metadata.UnityVersion, data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Failed to get Unity version for package " + data.PackageName);
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return UnityVersion.MinVersion;
    }

    // Reads a serialized file (e.g. "globalgamemanagers") out of a loaded bundle without going
    // through AssetsManager.LoadAssetsFileFromBundle (which can wrongly reject newer formats).
    private static AssetsFileInstance? LoadSerializedFileFromBundle(AssetsManager assetsManager, BundleFileInstance bundle, string name)
    {
        int index = bundle.file.GetFileIndex(name);
        if (index < 0)
            return null;

        bundle.file.GetFileRange(index, out long offset, out long length);

        AssetsFileReader reader = bundle.file.DataReader;
        reader.Position = offset;
        byte[] serializedFileData = reader.ReadBytes((int)length);

        return assetsManager.LoadAssetsFile(new MemoryStream(serializedFileData), name, false);
    }

    private static UnityVersion TryParseUnityVersion(string version, UnityApplicationFinder.Data data)
    {
        if (!string.IsNullOrEmpty(version))
        {
            // Bundle/serialized-file versions carry a changeset suffix ("2022.3.45f1-378343")
            // that UnityVersion.Parse rejects; extract the canonical token first.
            string cleaned = version.Trim();
            Match match = UnityVersionToken.Match(cleaned);
            if (match.Success)
                cleaned = match.Value;

            try
            {
                return UnityVersion.Parse(cleaned);
            }
            catch { }
        }

        System.Diagnostics.Debug.WriteLine($"Package {data.PackageName} has unparsable version of {version}");
        return UnityVersion.MinVersion;
    }
}
