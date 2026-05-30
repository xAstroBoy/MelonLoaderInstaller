using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace MelonLoader.Installer.Core.PatchSteps;

internal class DetectUnityVersion : IPatchStep
{
    // Canonical Unity engine version token: <major>.<minor>.<patch><type><build>
    // (type: a=alpha b=beta c=china f=final p=patch x=experimental).
    private static readonly Regex UnityVersionToken = new(@"\d+\.\d+\.\d+[abcfpx]\d+", RegexOptions.Compiled);

    public bool Run(Patcher patcher)
    {
        if (patcher.Args.UnityVersion != null && patcher.Args.UnityVersion != UnityVersion.MinVersion)
            return true;

        using FileStream apkStream = new(patcher.Info.OutputBaseApkPath, FileMode.Open);
        using ZipArchive archive = new(apkStream, ZipArchiveMode.Read);

        AssetsManager uAssetsManager = new();

        // Try to read directly from a loose globalgamemanagers (older Unity layout).
        try
        {
            ZipArchiveEntry? assetEntry = archive.GetEntry("assets/bin/Data/globalgamemanagers");
            if (assetEntry != null)
            {
                AssetsFileInstance instance = uAssetsManager.LoadAssetsFile(ReadEntryToSeekableStream(assetEntry), "/bin/Data/globalgamemanagers", true);
                if (TrySetVersion(patcher, instance.file.Metadata.UnityVersion))
                    return true;
            }
        }
        catch { }

        // Otherwise read globalgamemanagers out of the data.unity3d bundle (modern Il2Cpp layout).
        try
        {
            ZipArchiveEntry assetEntry = archive.GetEntry("assets/bin/Data/data.unity3d")!;

            // ZipArchive entry streams are forward-only, but AssetsTools.NET needs to seek while
            // reading/unpacking the bundle, so buffer the entry into a seekable MemoryStream first.
            BundleFileInstance bundle = uAssetsManager.LoadBundleFile(ReadEntryToSeekableStream(assetEntry), "/bin/Data/data.unity3d");

            // Avoid AssetsManager.LoadAssetsFileFromBundle: its IsAssetsFile() heuristic returns a
            // false negative for SerializedFile format >= 0x16 (Unity 2017+/2020+/2022+), so it
            // returns null and detection fails. Read the serialized file's bytes directly instead.
            AssetsFileInstance? instance = LoadSerializedFileFromBundle(uAssetsManager, bundle, "globalgamemanagers");
            if (instance != null && TrySetVersion(patcher, instance.file.Metadata.UnityVersion))
                return true;

            throw new Exception("Could not read 'globalgamemanagers' out of data.unity3d.");
        }
        catch (Exception ex)
        {
            patcher.Logger.Log("Failed to get Unity version, cannot patch.\n" + ex.ToString());
            patcher.Args.UnityVersion = UnityVersion.MinVersion;
            return false;
        }
    }

    private static MemoryStream ReadEntryToSeekableStream(ZipArchiveEntry entry)
    {
        MemoryStream ms = new();
        using (Stream s = entry.Open())
            s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

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

    // Unity version strings from bundles/serialized files often carry a changeset suffix
    // (e.g. "2022.3.45f1-378343") that UnityVersion.Parse rejects. Extract the canonical token.
    private static bool TrySetVersion(Patcher patcher, string rawVersion)
    {
        if (string.IsNullOrEmpty(rawVersion))
            return false;

        string cleaned = rawVersion.Trim();
        Match match = UnityVersionToken.Match(cleaned);
        if (match.Success)
            cleaned = match.Value;

        try
        {
            UnityVersion version = UnityVersion.Parse(cleaned);
            if (version == UnityVersion.MinVersion)
                return false;

            patcher.Args.UnityVersion = version;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
