﻿using System.Buffers;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ModTools.Shared;

internal sealed class AssetBundleHelper : IDisposable
{
    private readonly AssetsManager manager;
    private readonly BundleFileInstance bundleInstance;
    private readonly List<AssetsFileInstance> fileInstances = [];

    public IList<AssetsFileInstance> FileInstances => fileInstances;

    public string Path => this.bundleInstance.path;

    private AssetBundleHelper(AssetsManager manager, BundleFileInstance bundleInstance)
    {
        this.manager = manager;
        this.bundleInstance = bundleInstance;

        foreach (
            (string name, int idx) in bundleInstance
                .file.GetAllFileNames()
                .Select((x, idx) => (x, idx))
        )
        {
            if (name.EndsWith(".resS", StringComparison.InvariantCultureIgnoreCase))
            {
                ConsoleApp.Log($"Skipping streamed assets file instance {name} at index {idx}");
                continue;
            }

            AssetsFileInstance instance;
            try
            {
                instance = manager.LoadAssetsFileFromBundle(
                    bundleInstance,
                    index: idx,
                    loadDeps: false
                );
            }
            catch
            {
                ConsoleApp.LogError($"[ERROR] Failed to load file instance {name} at index {idx}");
                throw;
            }

            if (instance == null)
            {
                ConsoleApp.LogError($"[WARNING] Skipping null file instance {name} at index {idx}");
                continue;
            }

            this.fileInstances.Add(instance);
        }
    }

    public static AssetBundleHelper FromPath(string path)
    {
        if (SharedOptionContext.ReadFromDisk)
        {
            // Read from disk instead
            AssetsManager manager = new();

#pragma warning disable CA2000 // CA2000: Dispose objects before losing scope. Ownership of the stream is transferred to the AssetBundleHelper, which will dispose of it in its own Dispose method.
            BundleFileInstance bundleFileInstance = new(File.OpenRead(path), unpackIfPacked: true);
#pragma warning restore CA2000

            return new AssetBundleHelper(manager, bundleFileInstance);
        }

        return FromData(File.ReadAllBytes(path), path);
    }

    public static AssetBundleHelper FromPathEncrypted(string path)
    {
        FileInfo fileInfo = new(path);
        int fileSize = checked((int)fileInfo.Length);

        byte[] encryptedArray = ArrayPool<byte>.Shared.Rent(fileSize);
        Span<byte> encryptedSpan = new(encryptedArray, 0, fileSize);

        using FileStream encryptedFs = File.OpenRead(path);
        int bytesRead = encryptedFs.Read(encryptedSpan);

        if (bytesRead < fileSize)
        {
            throw new IOException(
                $"Failed to read all of the file: read {bytesRead} bytes, but expected {fileSize} bytes"
            );
        }

        byte[] data = RijndaelHelper.Decrypt(encryptedSpan);

        ArrayPool<byte>.Shared.Return(encryptedArray);

        /* We could respect GlobalOptionContext.LowMemoryMode here and write the decrypted data to a temporary file,
         * then use that as the backing stream, but this is only called once per command and is unlikely to make up
         * any significant proportion of the memory usage. */

        return FromData(data, path);
    }

    private static AssetBundleHelper FromData(byte[] data, string path)
    {
        MemoryStream bundleMemoryStream = new(data);

        AssetsManager manager = new();
        BundleFileInstance bundleFileInstance = new(bundleMemoryStream, path, unpackIfPacked: true);

        return new AssetBundleHelper(manager, bundleFileInstance);
    }

    public IEnumerable<string> GetContainerNames(int fileIndex = 0)
    {
        AssetsFileInstance assetsFileInstance = this.fileInstances[fileIndex];

        var assetBundleInfo = this.manager.GetBaseField(
            assetsFileInstance,
            assetsFileInstance.file.GetAssetInfo(pathId: 1)
        );

        var container = assetBundleInfo["m_Container.Array"];
        foreach (var containerChild in container.Children)
        {
            string name = containerChild[0].AsString;
            yield return name;
        }
    }

    public AssetTypeValueField GetBaseField(string assetName, int fileIndex = 0)
    {
        AssetFileInfo fileInfo = this.GetFileInfo(assetName, fileIndex);

        return this.GetBaseField(fileInfo);
    }

    public AssetTypeValueField GetBaseField(AssetFileInfo fileInfo, int fileIndex = 0)
    {
        return this.manager.GetBaseField(this.FileInstances[fileIndex], fileInfo);
    }

    public void UpdateBaseField(string assetName, AssetTypeValueField newField, int fileIndex = 0)
    {
        AssetFileInfo fileInfo = this.GetFileInfo(assetName, fileIndex);
        fileInfo.SetNewData(newField);
    }

    public void Write(Stream stream)
    {
        for (int i = 0; i < FileInstances.Count; i++)
        {
            this.bundleInstance.file.BlockAndDirInfo.DirectoryInfos[i]
                .SetNewData(this.FileInstances[i].file);
        }

        int streamLength = checked((int)this.bundleInstance.BundleStream.Length);

        using MemoryStream decompStream = new(streamLength);
        using AssetsFileWriter decompWriter = new(decompStream);
        this.bundleInstance.file.Write(decompWriter);

        AssetBundleFile newUncompressedBundle = new();
        using AssetsFileReader reader = new(decompStream);
        newUncompressedBundle.Read(reader);
        using AssetsFileWriter writer = new(stream);
        newUncompressedBundle.Pack(writer, AssetBundleCompressionType.LZ4);
    }

    private AssetFileInfo GetFileInfo(string assetName, int fileIndex)
    {
        AssetsFileInstance assetsFile = this.FileInstances[fileIndex];
        AssetsFileReader reader = assetsFile.file.Reader;
        AssetFileInfo? result = null;

        foreach (
            AssetFileInfo assetFileInfo in assetsFile.file.GetAssetsOfType(
                AssetClassID.MonoBehaviour
            )
        )
        {
            long filePosition = assetFileInfo.GetAbsoluteByteOffset(assetsFile.file);

            // This trick (from UABEA) only works for MonoBehaviours with m_Name set, i.e. only scriptable object
            // MonoBehaviours.
            reader.Position = filePosition + 0x1c;
            string readAssetName = reader.ReadCountStringInt32();

            if (readAssetName == assetName)
            {
                result = assetFileInfo;
                break;
            }
        }

        if (result == null)
        {
            throw new ArgumentException(
                $"Could not find any MonoBehaviour assets with m_Name == {assetName}"
            );
        }

        return result;
    }

    public void Dispose()
    {
        this.bundleInstance.BundleStream.Dispose();
    }
}
