using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace FnExtractor;

public static class Program
{
    private const string GameDirectory = @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Content\Paks";
    private const string MappingsUrl = "https://api.fortniteapi.com/v1/mappings";
    private const string AesUrl = "https://api.fortniteapi.com/v1/aes";
    private const string ExportDirectory = "./exports";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(theme: AnsiConsoleTheme.Literate)
            .WriteTo.File("logs/extractor-.log", rollingInterval: RollingInterval.Day)
            .MinimumLevel.Information()
            .CreateLogger();

        var pngEntries = ReadConfigFile("png.txt");
        var jsonEntries = ReadConfigFile("json.txt");
        var exportAssetList = args.Contains("--assets", StringComparer.OrdinalIgnoreCase);

        if (pngEntries.Count == 0 && jsonEntries.Count == 0 && !exportAssetList)
        {
            Log.Error("No entries configured in png.txt or json.txt. Use --assets to export asset list. Exiting.");
            return;
        }

        Log.Information("PNG entries: {Count}, JSON entries: {Count2}", pngEntries.Count, jsonEntries.Count);

        OodleHelper.Initialize(OodleHelper.OODLE_NAME_CURRENT);
        TextureDecoder.UseAssetRipperTextureDecoder = true;

        using var httpClient = new HttpClient();

        Log.Information("Fetching AES keys...");
        var aesResponse = await FetchJson<AesResponse>(httpClient, AesUrl);
        if (aesResponse == null) { Log.Error("No AES keys available. Exiting."); return; }
        Log.Information("AES version: {Version}", aesResponse.Version);

        Log.Information("Fetching mappings...");
        var mappingsBytes = await FetchMappings(httpClient);
        if (mappingsBytes == null) { Log.Error("No mappings available. Exiting."); return; }

        // Setup provider
        var version = new VersionContainer(EGame.GAME_UE5_LATEST, ETexturePlatform.DesktopMobile);
        var provider = new DefaultFileProvider(GameDirectory, SearchOption.TopDirectoryOnly, version);
        provider.ReadNaniteData = false;
        provider.SkipReferencedTextures = true;
        provider.MappingsContainer = new InMemoryUsmapProvider(mappingsBytes);

        provider.Initialize();
        SubmitKeys(provider, aesResponse);
        provider.PostMount();

        try { provider.LoadLocalization(ELanguage.English); }
        catch (KeyNotFoundException) { Log.Warning("Localization data not available, skipping"); }

        // Build folder index for batch lookups
        var folderIndex = provider.Files.Values
            .GroupBy(f => GetFolder(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();

        if (exportAssetList)
        {
            var allPaths = provider.Files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            await File.WriteAllLinesAsync("assets.txt", allPaths);
            Log.Information("Written {Count} asset paths to assets.txt", allPaths.Count);
        }

        var pngCount = await ExportPngs(provider, pngEntries, folderIndex);
        var jsonCount = await ExportJsons(provider, jsonEntries, folderIndex);

        stopwatch.Stop();
        Log.Information("Done! Exported {PngCount} PNGs and {JsonCount} JSONs to {Dir} in {Elapsed}",
            pngCount, jsonCount, Path.GetFullPath(ExportDirectory), stopwatch.Elapsed);

        provider.Dispose();
    }

    // --- Export logic ---

    private static async Task<int> ExportPngs(DefaultFileProvider provider,
        List<string> entries, Dictionary<string, GameFile[]> folderIndex)
    {
        var count = 0;
        foreach (var file in ResolveFiles(provider, entries, folderIndex))
        {
            try
            {
                if (!provider.TryLoadPackage(file, out var package)) continue;

                foreach (var texture in package.GetExports().OfType<UTexture>())
                {
                    var bitmap = texture.Decode(provider.Versions.Platform);
                    if (bitmap == null) continue;

                    var encoded = bitmap.Encode(ETextureFormat.Png, false, out _);
                    var outputPath = Path.Combine(ExportDirectory, GetFolder(file.Path), $"{texture.Name}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await File.WriteAllBytesAsync(outputPath, encoded);

                    count++;
                    Log.Information("Exported PNG: {Name}", texture.Name);
                }
            }
            catch (Exception e)
            {
                Log.Warning("Failed to process {Path}: {Error}", file.Path, e.Message);
            }
        }
        return count;
    }

    private static async Task<int> ExportJsons(DefaultFileProvider provider,
        List<string> entries, Dictionary<string, GameFile[]> folderIndex)
    {
        var count = 0;
        foreach (var file in ResolveFiles(provider, entries, folderIndex))
        {
            try
            {
                string json;
                if (file.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                {
                    if (!file.TryCreateReader(out var archive)) continue;
                    var locres = new FTextLocalizationResource(archive);
                    var locData = locres.Entries.ToDictionary(
                        ns => ns.Key.Str,
                        ns => ns.Value.ToDictionary(e => e.Key.Str, e => e.Value.LocalizedString));
                    json = JsonConvert.SerializeObject(locData, JsonSettings);
                    Log.Information("Exported locres JSON: {Name} ({Count} namespaces)", file.NameWithoutExtension, locData.Count);
                }
                else
                {
                    if (!provider.TryLoadPackage(file, out var package)) continue;
                    var exports = package.GetExports();
                    if (!exports.Any()) continue;

                    try { json = JsonConvert.SerializeObject(exports, JsonSettings); }
                    catch (Exception serEx)
                    {
                        Log.Warning("Failed to serialize {Path}: {Error}", file.Path, serEx.Message);
                        continue;
                    }
                    Log.Information("Exported JSON: {Name}", file.NameWithoutExtension);
                }

                var outputPath = Path.Combine(ExportDirectory, GetFolder(file.Path), $"{file.NameWithoutExtension}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, json);
                count++;
            }
            catch (Exception e)
            {
                Log.Warning("Failed to process {Path}: {Error}", file.Path, e.Message);
            }
        }
        return count;
    }

    // --- File resolution ---

    private static IEnumerable<GameFile> ResolveFiles(DefaultFileProvider provider,
        List<string> entries, Dictionary<string, GameFile[]> folderIndex)
    {
        foreach (var entry in entries)
        {
            var singleFile = ResolveFile(provider, entry);
            if (singleFile != null)
            {
                yield return singleFile;
                continue;
            }

            // Treat as folder prefix
            var matchingFolders = folderIndex.Keys
                .Where(k => k.StartsWith(entry, StringComparison.OrdinalIgnoreCase) ||
                            k.Equals(entry, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Log.Information("Processing folder: {Folder} ({Count} subfolders)", entry, matchingFolders.Count);

            if (matchingFolders.Count == 0)
                Log.Warning("Entry not found as file or folder: {Entry}", entry);

            foreach (var folder in matchingFolders)
                foreach (var file in folderIndex[folder])
                    yield return file;
        }
    }

    private static GameFile? ResolveFile(DefaultFileProvider provider, string path)
    {
        if (provider.Files.TryGetValue(path, out var file))
            return file;

        var match = provider.Files.Keys
            .FirstOrDefault(k => k.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase));
        return match != null ? provider.Files[match] : null;
    }

    // --- AES & Mappings ---

    private static async Task<byte[]?> FetchMappings(HttpClient client)
    {
        try
        {
            var json = await client.GetStringAsync(MappingsUrl);
            var mappings = JsonConvert.DeserializeObject<List<MappingsEntry>>(json);
            var entry = mappings?.FirstOrDefault();
            if (entry?.Url == null) { Log.Error("No mappings entry found"); return null; }

            Log.Information("Downloading mappings: {File}", entry.FileName);
            return await client.GetByteArrayAsync(entry.Url);
        }
        catch (Exception e) { Log.Error(e, "Error fetching mappings"); return null; }
    }

    private static void SubmitKeys(DefaultFileProvider provider, AesResponse aes)
    {
        var keys = new Dictionary<FGuid, FAesKey> { { new FGuid(), new FAesKey(aes.MainKey) } };

        if (aes.DynamicKeys != null)
        {
            foreach (var dk in aes.DynamicKeys)
            {
                try { keys[new FGuid(dk.Guid)] = new FAesKey(dk.Key); }
                catch (Exception e) { Log.Warning("Failed to parse dynamic key {Guid}: {Error}", dk.Guid, e.Message); }
            }
        }

        var mounted = provider.SubmitKeys(keys);
        Log.Information("Submitted {Keys} keys, mounted {Mounted} archives", keys.Count, mounted);

        if (provider.UnloadedVfs.Count > 0)
        {
            Log.Warning("{Count} archive(s) could not be mounted:", provider.UnloadedVfs.Count);
            foreach (var r in provider.UnloadedVfs)
                Log.Warning("  - {Name} (keyGuid: {Guid})", r.Name, r.EncryptionKeyGuid);
        }
    }

    // --- Helpers ---

    private static async Task<T?> FetchJson<T>(HttpClient client, string url) where T : class
    {
        try { return JsonConvert.DeserializeObject<T>(await client.GetStringAsync(url)); }
        catch (Exception e) { Log.Error(e, "Error fetching {Url}", url); return null; }
    }

    private static string GetFolder(string path)
        => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static List<string> ReadConfigFile(string fileName)
    {
        if (!File.Exists(fileName)) { Log.Warning("Config file {File} not found", fileName); return []; }
        return File.ReadAllLines(fileName)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Replace('\\', '/').TrimEnd('/'))
            .ToList();
    }
}

// --- Supporting types ---

public class InMemoryUsmapProvider : UsmapTypeMappingsProvider
{
    private readonly byte[] _data;
    public InMemoryUsmapProvider(byte[] data) { _data = data; Load(data); }
    public override void Reload() => Load(_data);
}

public class AesResponse
{
    [JsonProperty("version")] public string? Version { get; set; }
    [JsonProperty("mainKey")] public string MainKey { get; set; } = "";
    [JsonProperty("dynamicKeys")] public List<DynamicKey>? DynamicKeys { get; set; }
}

public class DynamicKey
{
    [JsonProperty("guid")] public string Guid { get; set; } = "";
    [JsonProperty("key")] public string Key { get; set; } = "";
}

public class MappingsEntry
{
    [JsonProperty("fileName")] public string? FileName { get; set; }
    [JsonProperty("url")] public string? Url { get; set; }
    [JsonProperty("hash")] public string? Hash { get; set; }
}
