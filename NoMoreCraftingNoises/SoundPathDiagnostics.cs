using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.Sound;

namespace NoMoreCraftingNoises;

/// <summary>
/// Opt-in diagnostics that records sound resource paths and sound indices.
/// It does not suppress or alter any sound. The native signatures and resource
/// layout are version-sensitive and are based on the legacy example in /old.
/// </summary>
internal sealed unsafe class SoundPathDiagnostics : IDisposable
{
    private const string PlaySpecificSoundSignature = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";
    private const string GetResourceSyncSignature = "E8 ?? ?? ?? ?? 48 8B C8 8B C3 F0 0F C0 81";
    private const string GetResourceAsyncSignature = "E8 ?? ?? ?? 00 48 8B D8 EB ?? F0 FF 83 ?? ?? 00 00";
    private const string LoadSoundFileSignature = "E8 ?? ?? ?? ?? 48 85 C0 75 12 B0 F6";
    private const int ResourceDataPointerOffset = 176;
    private const int MaxCapturedPaths = 500;
    private const int MaxActiveSoundNodes = 256;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct GetResourceParameters
    {
        [FieldOffset(16)] public uint SegmentOffset;
        [FieldOffset(20)] public uint SegmentLength;
    }

    private delegate void* PlaySpecificSoundDelegate(long soundInfo, int soundIndex);
    private delegate ResourceHandle* GetResourceSyncDelegate(ResourceManager* resourceManager, ResourceCategory* categoryId, uint* resourceType, int* resourceHash, byte* path, GetResourceParameters* parameters, nint unknown8, uint unknown9);
    private delegate ResourceHandle* GetResourceAsyncDelegate(ResourceManager* resourceManager, ResourceCategory* categoryId, uint* resourceType, int* resourceHash, byte* path, GetResourceParameters* parameters, byte unknown7, nint unknown8, uint unknown9);
    private delegate nint LoadSoundFileDelegate(nint resourceHandle, uint unknown);

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly HashSet<string> mappedSoundPaths;
    private readonly ConcurrentDictionary<nint, string> soundPathsByData = new();
    private readonly ConcurrentDictionary<string, byte> capturedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> capturedCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> suppressedSounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<nint> mutedSoundDataPointers = new();

    private Hook<PlaySpecificSoundDelegate>? playSpecificSoundHook;
    private Hook<GetResourceSyncDelegate>? getResourceSyncHook;
    private Hook<GetResourceAsyncDelegate>? getResourceAsyncHook;
    private Hook<LoadSoundFileDelegate>? loadSoundFileHook;
    private bool captureEnabled;

    public SoundPathDiagnostics(ISigScanner sigScanner, IGameInteropProvider interopProvider, IFramework framework, IPluginLog log, string mappingsFilePath)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.framework = framework;
        this.log = log;
        mappedSoundPaths = LoadMappedSoundPaths(mappingsFilePath);
    }

    public bool IsEnabled { get; private set; }
    public bool IsCapturing => captureEnabled;
    public bool IsFilterActive => IsEnabled;

    public IReadOnlyList<string> CapturedPaths => capturedPaths.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> CapturedCategories => capturedCategories.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> SuppressedSounds => suppressedSounds.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    public int MappedSoundPathCount => mappedSoundPaths.Count;

    public bool Enable() => EnableHooks();

    public void SetCaptureEnabled(bool enabled)
    {
        captureEnabled = enabled;
        if (enabled && !EnableHooks())
            captureEnabled = false;
    }

    private bool EnableHooks()
    {
        if (IsEnabled)
            return true;

        if (!TryScan(PlaySpecificSoundSignature, out var playAddress)
            || !TryScan(GetResourceSyncSignature, out var syncAddress)
            || !TryScan(GetResourceAsyncSignature, out var asyncAddress)
            || !TryScan(LoadSoundFileSignature, out var loadAddress))
        {
            log.Warning("Sound path diagnostics could not start: one or more legacy signatures were not found. No hooks were enabled.");
            return false;
        }

        try
        {
            playSpecificSoundHook = interopProvider.HookFromAddress<PlaySpecificSoundDelegate>(playAddress, OnPlaySpecificSound);
            getResourceSyncHook = interopProvider.HookFromAddress<GetResourceSyncDelegate>(syncAddress, OnGetResourceSync);
            getResourceAsyncHook = interopProvider.HookFromAddress<GetResourceAsyncDelegate>(asyncAddress, OnGetResourceAsync);
            loadSoundFileHook = interopProvider.HookFromAddress<LoadSoundFileDelegate>(loadAddress, OnLoadSoundFile);

            playSpecificSoundHook.Enable();
            getResourceSyncHook.Enable();
            getResourceAsyncHook.Enable();
            loadSoundFileHook.Enable();
            framework.Update += OnFrameworkUpdate;
            IsEnabled = true;
            log.Information("Sound hooks enabled for configured filtering and/or diagnostics capture.");
            return true;
        }
        catch (Exception ex)
        {
            DisposeHooks();
            log.Error(ex, "Failed to enable sound path diagnostics; hooks were disposed.");
            return false;
        }
    }

    private void DisableHooks()
    {
        framework.Update -= OnFrameworkUpdate;
        DisposeHooks();
        IsEnabled = false;
    }

    public void ClearCapturedPaths()
    {
        capturedPaths.Clear();
        capturedCategories.Clear();
        suppressedSounds.Clear();
    }

    public void Dispose()
    {
        captureEnabled = false;
        DisableHooks();
    }

    private bool TryScan(string signature, out nint address)
    {
        return sigScanner.TryScanText(signature, out address) && address != nint.Zero;
    }

    private void DisposeHooks()
    {
        playSpecificSoundHook?.Dispose();
        getResourceSyncHook?.Dispose();
        getResourceAsyncHook?.Dispose();
        loadSoundFileHook?.Dispose();
        playSpecificSoundHook = null;
        getResourceSyncHook = null;
        getResourceAsyncHook = null;
        loadSoundFileHook = null;
    }

    private void* OnPlaySpecificSound(long soundInfo, int soundIndex)
    {
        try
        {
            if (soundInfo != 0)
            {
                var soundData = *(byte**)(soundInfo + 8);
                if (soundData != null && soundPathsByData.TryGetValue((nint)soundData, out var path))
                {
                    if (captureEnabled)
                        CapturePath(path, soundIndex);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error recording a sound path.");
        }

        return playSpecificSoundHook!.Original(soundInfo, soundIndex);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            var soundManager = SoundManager.Instance();
            if (soundManager == null)
                return;

            var current = (ISoundData*)soundManager->ActiveSoundDataListHead;
            var visited = 0;
            var activePointers = new HashSet<nint>();
            while (current != null && visited++ < MaxActiveSoundNodes)
            {
                if (current->IsActive)
                {
                    activePointers.Add((nint)current);
                    var sound = (SoundData*)current;
                    var handle = sound->SoundResourceHandle;
                    if (handle != null)
                    {
                        var path = ((ResourceHandle*)handle)->FileName.ToString();
                        if (IsMappedSoundPath(path))
                        {
                            var category = sound->VolumeCategory.ToString();
                            var localPlayer = sound->IsLocalPlayer ? "local" : "not-local";
                            var isSuppressed = sound->VolumeCategory == SoundVolumeCategory.Other
                                               && IsCraftingSpecificMappedSoundPath(path);
                            var row = $"{NormalizePath(path)}/{sound->SoundNumber} | {category} | {localPlayer}";
                            if (captureEnabled && capturedCategories.Count < MaxCapturedPaths)
                                capturedCategories.TryAdd(isSuppressed ? $"{row} | muted" : row, 0);

                            if (isSuppressed && mutedSoundDataPointers.Add((nint)current))
                            {
                                current->Stop(0);
                                if (suppressedSounds.Count < MaxCapturedPaths)
                                    suppressedSounds.TryAdd($"{NormalizePath(path)}/{sound->SoundNumber}", 0);
                            }
                        }
                    }
                }

                current = current->Next;
            }

            mutedSoundDataPointers.IntersectWith(activePointers);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error inspecting active sound metadata.");
        }
    }

    private void CapturePath(string path, int soundIndex)
    {
        if (!IsMappedSoundPath(path) || capturedPaths.Count >= MaxCapturedPaths)
            return;

        capturedPaths.TryAdd($"{NormalizePath(path)}/{soundIndex}", 0);
    }

    private bool IsMappedSoundPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = NormalizePath(path);
        if (mappedSoundPaths.Contains(normalized))
            return true;

        var soundPathStart = normalized.IndexOf("sound/", StringComparison.OrdinalIgnoreCase);
        return soundPathStart >= 0 && mappedSoundPaths.Contains(normalized[soundPathStart..]);
    }

    private bool IsCraftingSpecificMappedSoundPath(string path)
    {
        var normalized = NormalizePath(path);
        if (!IsMappedSoundPath(normalized) || normalized == "sound/vfx/se_vfx_common.scd")
            return false;

        if (normalized.StartsWith("sound/battle/live/se_craft_", StringComparison.OrdinalIgnoreCase))
            return true;

        return normalized.StartsWith("sound/vfx/live/se_vfx_live_", StringComparison.OrdinalIgnoreCase)
               && (normalized.Contains("makeball_", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("make_finish_", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("baf_craft", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("action_success", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("action_great", StringComparison.OrdinalIgnoreCase)
                   || normalized.Contains("action_fail", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private HashSet<string> LoadMappedSoundPaths(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                log.Warning($"Sound mapping file not found: {path}. Capture will be empty until mappings.json is deployed beside the plugin.");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            var paths = mappings?.Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            log.Information($"Loaded {paths.Count} sound paths from mappings.json.");
            return paths;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Could not load mappings.json; sound-path capture will remain empty.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private ResourceHandle* OnGetResourceSync(ResourceManager* resourceManager, ResourceCategory* categoryId, uint* resourceType, int* resourceHash, byte* path, GetResourceParameters* parameters, nint unknown8, uint unknown9)
    {
        var result = getResourceSyncHook!.Original(resourceManager, categoryId, resourceType, resourceHash, path, parameters, unknown8, unknown9);
        RememberSoundResource(result, path);
        return result;
    }

    private ResourceHandle* OnGetResourceAsync(ResourceManager* resourceManager, ResourceCategory* categoryId, uint* resourceType, int* resourceHash, byte* path, GetResourceParameters* parameters, byte unknown7, nint unknown8, uint unknown9)
    {
        var result = getResourceAsyncHook!.Original(resourceManager, categoryId, resourceType, resourceHash, path, parameters, unknown7, unknown8, unknown9);
        RememberSoundResource(result, path);
        return result;
    }

    private nint OnLoadSoundFile(nint resourceHandle, uint unknown)
    {
        var result = loadSoundFileHook!.Original(resourceHandle, unknown);
        try
        {
            if (resourceHandle != nint.Zero)
            {
                var path = ((ResourceHandle*)resourceHandle)->FileName.ToString();
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                {
                    var soundData = Marshal.ReadIntPtr(resourceHandle + ResourceDataPointerOffset);
                    if (soundData != nint.Zero)
                        soundPathsByData[soundData] = path;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error mapping a loaded sound resource.");
        }

        return result;
    }

    private void RememberSoundResource(ResourceHandle* resourceHandle, byte* path)
    {
        if (resourceHandle == null || path == null)
            return;

        try
        {
            var resourcePath = ReadNullTerminatedUtf8(path);
            if (!resourcePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                return;

            var soundData = Marshal.ReadIntPtr((nint)resourceHandle + ResourceDataPointerOffset);
            if (soundData != nint.Zero)
                soundPathsByData[soundData] = resourcePath;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error mapping a sound resource path.");
        }
    }

    private static string ReadNullTerminatedUtf8(byte* pointer)
    {
        if (pointer == null)
            return string.Empty;

        var length = 0;
        while (length < 1024 && pointer[length] != 0)
            length++;

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(pointer, length));
    }
}
