using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace NoMoreCraftingNoises.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("No More Crafting Noises###NoMoreCraftingNoisesMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 390),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text("Crafting audio filter");
        ImGui.Separator();

        var filterEnabled = plugin.SoundDiagnostics.IsFilterActive;
        ImGui.Text(filterEnabled ? "Filter: ON" : "Filter: OFF");
        ImGui.SameLine();
        ImGui.TextDisabled($"{plugin.SoundDiagnostics.MappedSoundPathCount} mapped paths");

        ImGui.TextWrapped("The filter starts automatically and stops mapped crafting-specific sounds classified as Other. Shared banks such as se_vfx_common.scd are excluded because they contain unrelated effects. Player-category sounds are left alone.");
        if (!filterEnabled)
            ImGui.TextWrapped("The filter could not start. Check the plugin log for signature or hook errors.");

        if (plugin.SoundDiagnostics.IsCapturing)
        {
            if (ImGui.Button("Stop diagnostics"))
                plugin.SoundDiagnostics.SetCaptureEnabled(false);
        }
        else if (ImGui.Button("Start diagnostics"))
        {
            plugin.SoundDiagnostics.SetCaptureEnabled(true);
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear report"))
            plugin.SoundDiagnostics.ClearCapturedPaths();

        var capturedPaths = plugin.SoundDiagnostics.CapturedPaths;
        var capturedCategories = plugin.SoundDiagnostics.CapturedCategories;
        var suppressedSounds = plugin.SoundDiagnostics.SuppressedSounds;
        ImGui.SameLine();
        if (ImGui.Button("Copy report"))
        {
            var summaryLines = new List<string>
            {
                $"Filter active: {plugin.SoundDiagnostics.IsFilterActive}",
                $"Mapped paths: {plugin.SoundDiagnostics.MappedSoundPathCount}",
                $"Observed playback paths ({capturedPaths.Count}):",
            };
            summaryLines.AddRange(capturedPaths);
            summaryLines.Add($"Observed active sound categories ({capturedCategories.Count}):");
            summaryLines.AddRange(capturedCategories);
            summaryLines.Add($"Suppressed mapped sounds ({suppressedSounds.Count}):");
            summaryLines.AddRange(suppressedSounds);
            ImGui.SetClipboardText(string.Join(Environment.NewLine, summaryLines));
        }

        ImGui.Spacing();
        ImGui.Text($"Diagnostics: {(plugin.SoundDiagnostics.IsCapturing ? "capturing" : "stopped")}  |  Observed {capturedPaths.Count} paths  |  Muted {suppressedSounds.Count} sounds");
        using var report = ImRaii.Child("CaptureReport", Vector2.Zero, true);
        if (!report.Success)
            return;

        if (capturedPaths.Count == 0 && capturedCategories.Count == 0 && suppressedSounds.Count == 0)
        {
            ImGui.TextDisabled("No matching sound activity captured yet.");
            return;
        }

        ImGui.Text("Observed playback paths");
        foreach (var path in capturedPaths.TakeLast(20))
            ImGui.BulletText(path);

        ImGui.Spacing();
        ImGui.Text("Observed sound categories");
        foreach (var sound in capturedCategories.TakeLast(30))
            ImGui.BulletText(sound);

        ImGui.Spacing();
        ImGui.Text("Muted sounds");
        foreach (var sound in suppressedSounds.TakeLast(20))
            ImGui.BulletText(sound);
    }
}
