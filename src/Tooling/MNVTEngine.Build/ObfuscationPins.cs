// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Runtime;
using Newtonsoft.Json;

namespace MNVTEngine.Build;

/// <summary>
/// The game's obfuscation rename map (symbol id → new name), stored next to the project so a later DLC build can pin
/// the game's names and keep its calls into the game resolving. Written by a JIT obfuscated game build and cleared by
/// any other, so a DLC never pins against names the shipped game does not actually use.
/// </summary>
public static class ObfuscationPins
{
    private static string Path(GameProject project, string assemblyName) =>
        System.IO.Path.Combine(project.RootDirectory, "Library", "Build", "Obfuscation", assemblyName + ".pin.json");

    /// <summary>Writes the map for <paramref name="assemblyName"/>, or deletes the file when <paramref name="pins"/> is null.</summary>
    public static void Write(GameProject project, string assemblyName, IReadOnlyDictionary<string, string>? pins)
    {
        ArgumentNullException.ThrowIfNull(project);
        var file = Path(project, assemblyName);
        if (pins is null || pins.Count == 0)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonConvert.SerializeObject(pins, Formatting.Indented));
    }

    /// <summary>The persisted map for <paramref name="assemblyName"/>, or null when the game shipped unobfuscated.</summary>
    public static IReadOnlyDictionary<string, string>? Read(GameProject project, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(project);
        var file = Path(project, assemblyName);
        if (!File.Exists(file))
        {
            return null;
        }

        var pins = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
        return pins is { Count: > 0 } ? pins : null;
    }
}
