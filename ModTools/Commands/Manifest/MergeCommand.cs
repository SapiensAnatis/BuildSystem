using System.Runtime.CompilerServices;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using ModTools.Shared;

namespace ModTools.Commands.Manifest;

internal sealed class MergeCommand
{
    /// <summary>
    /// Update the target manifest by adding any files only present in the source manifest.
    /// </summary>
    /// <param name="targetPath">--target|-t, The path to the manifest that is the target of the merge.</param>
    /// <param name="sourcePath">--source|-s, The path to the manifest that is the source of the merge.</param>
    /// <param name="outputManifestDir">--output-manifests|-m, The path to a directory to output the merged manifest to.</param>
    /// <param name="outputBundleDir">--output-bundles|-b, The path to a directory to output the new bundles to.</param>
    /// <param name="omissionsPath">--omissions| Path to a JSON file containing a list of asset names to avoid merging into the new manifest.</param>
    /// <param name="assetDirectories">--assets-path|-a, Comma-separated list of directories to source the added asset bundles from.</param>
    /// <param name="conversion">--convert|-c, Whether to convert assets to iOS during the merge process.</param>
    /// <param name="readFromDisk">Whether to decrease memory usage, at the expense of performance, by reading bundles directly from disk without loading them into memory first.</param>
    [Command("merge")]
    public void Command(
        string targetPath,
        string sourcePath,
        string outputManifestDir,
        string outputBundleDir,
        string? omissionsPath,
        string[] assetDirectories,
        bool conversion,
        bool readFromDisk
    )
    {
        SharedOptionContext.ReadFromDisk = readFromDisk;

        using AssetBundleHelper targetHelper = AssetBundleHelper.FromPathEncrypted(targetPath);
        using AssetBundleHelper sourceHelper = AssetBundleHelper.FromPathEncrypted(sourcePath);

        DirectoryInfo outputBundleDirInfo = new(outputBundleDir);

        AssetTypeValueField targetBaseField = targetHelper.GetBaseField("manifest");
        AssetTypeValueField sourceBaseField = sourceHelper.GetBaseField("manifest");

        string[]? omittedAssetNames = null;

        if (omissionsPath is not null)
        {
            using FileStream omitListFs = File.OpenRead(omissionsPath);
            omittedAssetNames = JsonSerializer.Deserialize(
                omitListFs,
                ModToolsSerializerContext.Default.StringArray
            );
        }

        List<AssetTypeValueField> othersToAdd = GetDiff(
            targetBaseField,
            sourceBaseField,
            omittedAssetNames,
            (manifest) => manifest["categories"]["Array"][1]["assets"]["Array"]
        );

        Directory.CreateDirectory(outputBundleDir);

        var assets = targetBaseField["categories"]["Array"][1]["assets"]["Array"].Children;

        var normalAsset = assets.First();

        foreach (AssetTypeValueField asset in othersToAdd)
        {
            string hash = asset["hash"].AsString;
            string assetPath = GetAssetPath(hash);
            // TODO multiple paths support again
            FileInfo bundleToAddPath = GetSourceFile(assetPath, assetDirectories);

#pragma warning disable CA2000 // Dispose objects before losing scope: Appears to be a false positive; there is a using declaration
            using AssetBundleHelper openedBundle = AssetBundleHelper.FromPath(
                bundleToAddPath.FullName
            );
#pragma warning restore CA2000

            if (asset["assets"].IsDummy)
            {
                var newArray = ValueBuilder.DefaultValueFieldFromTemplate(
                    normalAsset["assets"].TemplateField
                );
                asset.Children.Add(newArray);

                PopulateAssetArray(newArray, openedBundle);
            }

            if (conversion)
            {
                (FileInfo converted, string newHash) = PerformConversion(openedBundle, asset);
                string newAssetPath = GetAssetPath(newHash);
                CopyToOutput(converted, outputBundleDirInfo, newAssetPath);
            }
            else
            {
                CopyToOutput(bundleToAddPath, outputBundleDirInfo, assetPath);
            }
        }

        targetBaseField["categories"]["Array"][1]["assets"]["Array"].Children.AddRange(othersToAdd);

        List<AssetTypeValueField> rawsToAdd = GetDiff(
            targetBaseField,
            sourceBaseField,
            omittedAssetNames,
            (manifest) => manifest["rawAssets"]["Array"]
        );

        targetBaseField["rawAssets"]["Array"].Children.AddRange(rawsToAdd);

        targetHelper.UpdateBaseField("manifest", targetBaseField);

        Directory.CreateDirectory(outputManifestDir);

        string outputPath = Path.Join(
            Path.GetDirectoryName(outputManifestDir),
            Path.GetFileName(sourcePath)
        );

        MemoryStream ms = new();
        targetHelper.Write(ms);

        byte[] decrypted = ms.ToArray();
        byte[] encrypted = RijndaelHelper.Encrypt(decrypted);

        ConsoleApp.Log($"Writing output to {outputPath}");
        File.WriteAllBytes(outputPath, encrypted);
    }

    private static List<AssetTypeValueField> GetDiff(
        AssetTypeValueField target,
        AssetTypeValueField source,
        string[]? omittedAssetNames,
        Func<AssetTypeValueField, AssetTypeValueField> path,
        [CallerArgumentExpression(nameof(path))] string? pathName = null
    )
    {
        AssetTypeValueField targetAssets = path.Invoke(target);
        AssetTypeValueField sourceAssets = path.Invoke(source);

        IEnumerable<AssetTypeValueField> assetsToAddEnumerable = sourceAssets.Except(
            targetAssets,
            ManifestAssetComparer.Instance
        );

        if (omittedAssetNames is not null)
        {
            assetsToAddEnumerable = assetsToAddEnumerable.ExceptBy(
                omittedAssetNames,
                manifestAsset => manifestAsset["name"].AsString
            );
        }

        List<AssetTypeValueField> assetsToAdd = assetsToAddEnumerable.ToList();

        ConsoleApp.Log($"Found {assetsToAdd.Count} assets to add to {pathName}");

        return assetsToAdd;
    }

    private static string GetAssetPath(string hash)
    {
        return Path.Join(hash[..2], hash);
    }

    private static (FileInfo convertedPath, string newHash) PerformConversion(
        AssetBundleHelper bundle,
        AssetTypeValueField asset
    )
    {
        string tempFileName = Path.GetTempFileName();
        FileInfo outputFileInfo = new(tempFileName);

        BundleConversionHelper.ConvertToIos(bundle, outputFileInfo);
        string newHash = HashHelper.GetHash(outputFileInfo);
        asset["hash"].AsString = newHash;

        return (new FileInfo(tempFileName), newHash);
    }

    private static void CopyToOutput(
        FileInfo sourcePath,
        DirectoryInfo outputDirectory,
        string assetPath
    )
    {
        string newPath = Path.Join(outputDirectory.FullName, assetPath);
        string newDirectory =
            Path.GetDirectoryName(newPath)
            ?? throw new InvalidOperationException("Failed to get directory name");

        Directory.CreateDirectory(newDirectory);

        File.Copy(sourcePath.FullName, newPath, overwrite: true);
    }

    private static FileInfo GetSourceFile(string assetPath, IEnumerable<string> directories)
    {
        foreach (string directory in directories)
        {
            string path = Path.Join(directory, assetPath);
            if (File.Exists(path))
            {
                return new FileInfo(path);
            }
        }

        throw new IOException($"Failed to find asset {assetPath} in any configured directory");
    }

    private static void PopulateAssetArray(
        AssetTypeValueField newAssetVector,
        AssetBundleHelper helper
    )
    {
        var newArray = newAssetVector["Array"];

        var newElements = helper
            .GetContainerNames()
            .Select(containerName =>
            {
                string arrayValue = containerName
                    .Replace("assets/_gluonresources/", "", StringComparison.Ordinal)
                    .Replace("resources/", "", StringComparison.Ordinal);

                var newValue = ValueBuilder.DefaultValueFieldFromArrayTemplate(newArray);
                newValue.Value.AsString = arrayValue;
                return newValue;
            });

        newArray.Children.AddRange(newElements);
    }
}

file sealed class ManifestAssetComparer : IEqualityComparer<AssetTypeValueField>
{
    public static ManifestAssetComparer Instance { get; } = new();

    public bool Equals(AssetTypeValueField? x, AssetTypeValueField? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

#pragma warning disable CA1065 // Exceptions should not be raised in this type of method. Internal comparer & helps to identify logic flaws over returning false
        AssetTypeValueField xName = x["name"];
        if (xName.IsDummy)
            throw new ArgumentException("Not a manifest asset", nameof(x));
        AssetTypeValueField yName = y["name"];
        if (yName.IsDummy)
            throw new ArgumentException("Not a manifest asset", nameof(y));
#pragma warning restore CA1065

        return xName.AsString == yName.AsString;
    }

    public int GetHashCode(AssetTypeValueField obj)
    {
        AssetTypeValueField name = obj["name"];

        if (name.IsDummy)
            throw new ArgumentException("Not a manifest asset", nameof(obj));

        return name.AsString.GetHashCode(StringComparison.Ordinal);
    }
}
