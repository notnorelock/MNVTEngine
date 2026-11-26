// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>A file that is not an editor package that may be imported, with every problem found.</summary>
public sealed class EditorPackageException(string source, IReadOnlyList<string> problems)
    : Exception($"{source} is not an editor package that may be imported:\n  " + string.Join("\n  ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>What an editor package says about itself (its <c>bundle.json</c>).</summary>
public sealed record EditorPackageManifest(string Name, string Version, string? Description = null);

/// <summary>One file of a package: its area (<c>Assets</c>, <c>Scripts</c> or <c>Editor</c>) and its path inside that area.</summary>
public sealed record EditorPackageEntry(string Area, string Path, long Length);

/// <summary>
/// An editor package (docs/editor/packages.md): a <c>.mnvtbundle</c> of kind <see cref="Kind"/> that adds a kit to a
/// project, as a Unity package does: assets with their <c>.meta</c> files (so GUIDs, and references to them, survive),
/// C# sources for the game scripts and C# sources for the editor (windows, inspectors). Unlike a content bundle it
/// carries code, which runs in the editor: it is imported by a person who trusts its author, never loaded by a game.
/// Every file lives under <c>&lt;Area&gt;/&lt;Name&gt;/</c>, so a package keeps to its own folders and can be upgraded
/// or removed cleanly.
/// </summary>
public sealed partial class EditorPackageFile : IDisposable
{
    public const string Kind = "mnvt.package";
    public const string AssetsArea = "Assets";
    public const string ScriptsArea = "Scripts";
    public const string EditorArea = "Editor";
    public const int SchemaVersion = 1;
    public const long MaxBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 20_000;

    private const int MaxManifestBytes = 64 * 1024;
    private static readonly string[] Areas = [AssetsArea, ScriptsArea, EditorArea];

    /// <summary>MSBuild files: a package brings sources, never projects or build logic of its own.</summary>
    private static readonly string[] CodeExtensions = [".cs", ".md"];

    private static readonly string[] RefusedExtensions = [".csproj", ".props", ".targets", ".sln", ".slnx", ".mnvtproj"];

    private EditorPackageFile(MnvtPackage package, EditorPackageManifest manifest, IReadOnlyList<EditorPackageEntry> entries)
    {
        Package = package;
        Manifest = manifest;
        Entries = entries;
    }

    public MnvtPackage Package { get; }

    public EditorPackageManifest Manifest { get; }

    public IReadOnlyList<EditorPackageEntry> Entries { get; }

    /// <summary>A package name: what its folders are called (letters, digits, '.', '_' and '-', starting with a letter).</summary>
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 64 } && NamePattern().IsMatch(name);

    /// <summary>Opens and checks a package.</summary>
    /// <exception cref="EditorPackageException">Not an editor package (every problem is listed).</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static EditorPackageFile Open(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        MnvtPackage package;
        try
        {
            package = MnvtPackage.Open(file);
        }
        catch (PackageException ex)
        {
            throw new EditorPackageException(System.IO.Path.GetFileName(file), [ex.Message]);
        }

        try
        {
            var problems = new List<string>();
            var manifest = ReadManifest(package, problems);
            var entries = new List<EditorPackageEntry>();
            long bytes = 0;
            foreach (var entry in package.Entries)
            {
                bytes += entry.Length;
                if (entry.Path == Bundles.ContentBundleFile.ManifestEntry)
                {
                    continue;
                }

                var slash = entry.Path.IndexOf('/', StringComparison.Ordinal);
                var area = slash < 0 ? "" : entry.Path[..slash];
                var path = slash < 0 ? entry.Path : entry.Path[(slash + 1)..];
                if (!Areas.Contains(area, StringComparer.Ordinal))
                {
                    problems.Add($"{entry.Path}: packages hold only {Bundles.ContentBundleFile.ManifestEntry}, {AssetsArea}/, {ScriptsArea}/ and {EditorArea}/");
                }
                else if (!VirtualPath.TryNormalize(path, out var normalized, out var error) || normalized != path || path.Length > 260)
                {
                    problems.Add($"{entry.Path}: {error ?? "not a normalized relative path"}");
                }
                else if (manifest is not null && !path.StartsWith(manifest.Name + "/", StringComparison.Ordinal))
                {
                    problems.Add($"{entry.Path}: outside {area}/{manifest.Name}/ (a package keeps to its own folders)");
                }
                else if (RefusedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.Path}: project and build files are not allowed (packages bring sources; the project's own C# projects compile them)");
                }
                else if (area != AssetsArea && !CodeExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.Path}: {ScriptsArea}/ and {EditorArea}/ hold C# sources (.cs) and notes (.md) only");
                }
                else
                {
                    entries.Add(new EditorPackageEntry(area, path, entry.Length));
                }
            }

            if (entries.Count > MaxFiles)
            {
                problems.Add($"{entries.Count} files; at most {MaxFiles} are allowed");
            }

            if (bytes > MaxBytes)
            {
                problems.Add($"its files hold {bytes} bytes; at most {MaxBytes} are allowed");
            }

            if (problems.Count > 0)
            {
                throw new EditorPackageException(System.IO.Path.GetFileName(file), problems);
            }

            return new EditorPackageFile(package, manifest!, [.. entries.OrderBy(e => e.Area, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal)]);
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a package from <paramref name="sourceDirectory"/>, which holds <c>Assets/</c>, <c>Scripts/</c> and
    /// <c>Editor/</c> folders (any may be missing), each with one folder named after the package. <c>bin/</c> and
    /// <c>obj/</c> folders are skipped. The result is checked as <see cref="Open"/> checks it.
    /// </summary>
    /// <returns>The file written.</returns>
    /// <exception cref="EditorPackageException">What would be packaged is not a valid package.</exception>
    public static string Write(string sourceDirectory, EditorPackageManifest manifest, string outputFile)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(outputFile);
        var problems = new List<string>();
        if (!IsValidName(manifest.Name))
        {
            problems.Add($"name '{manifest.Name}': letters, digits, '.', '_' and '-', up to 64, starting with a letter");
        }

        if (manifest.Version is not { Length: > 0 and <= 32 })
        {
            problems.Add("version: 1 to 32 characters");
        }

        var files = Areas
            .Select(area => System.IO.Path.Combine(sourceDirectory, area))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(sourceDirectory, f).Replace('\\', '/')))
            .Where(p => !p.Split('/').Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            problems.Add($"{sourceDirectory} has no files under {string.Join(", ", Areas.Select(a => a + "/"))}");
        }

        if (problems.Count > 0)
        {
            throw new EditorPackageException(System.IO.Path.GetFileName(outputFile), problems);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputFile))!);
        var temporary = outputFile + ".tmp";
        var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}/{manifest.Name}/{manifest.Version}")).AsSpan(0, 16));
        using (var writer = new PackageWriter(temporary, new PackageWriterOptions(identity, Guid.NewGuid(),
            Properties: new Dictionary<string, string> { ["kind"] = "bundle", ["bundleKind"] = Kind })))
        {
            writer.Add(Bundles.ContentBundleFile.ManifestEntry, WriteManifest(manifest));
            foreach (var file in files)
            {
                writer.Add(file, File.ReadAllBytes(System.IO.Path.Combine(sourceDirectory, file)));
            }

            writer.Complete();
        }

        try
        {
            Open(temporary).Dispose();
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        File.Move(temporary, outputFile, overwrite: true);
        return outputFile;
    }

    /// <exception cref="IOException">The package data is damaged.</exception>
    public byte[] Read(EditorPackageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var stream = Package.OpenRead(entry.Area + "/" + entry.Path);
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    public void Dispose() => Package.Dispose();

    private static byte[] WriteManifest(EditorPackageManifest manifest) => Encoding.UTF8.GetBytes(new JObject
    {
        ["schemaVersion"] = SchemaVersion,
        ["kind"] = Kind,
        ["name"] = manifest.Name,
        ["version"] = manifest.Version,
        ["description"] = manifest.Description,
    }.ToString(Formatting.Indented));

    private static EditorPackageManifest? ReadManifest(MnvtPackage package, List<string> problems)
    {
        const string entry = Bundles.ContentBundleFile.ManifestEntry;
        if (!package.TryGetEntry(entry, out var info) || info.Length > MaxManifestBytes)
        {
            problems.Add($"{entry}: missing or larger than {MaxManifestBytes} bytes");
            return null;
        }

        JObject json;
        try
        {
            using var stream = package.OpenRead(entry);
            using var reader = new JsonTextReader(new StreamReader(stream, Encoding.UTF8)) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
            json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException ex)
        {
            problems.Add($"{entry}: invalid JSON: {ex.Message}");
            return null;
        }

        var before = problems.Count;
        if (json.Value<int?>("schemaVersion") is not SchemaVersion)
        {
            problems.Add($"{entry}: schemaVersion: {json["schemaVersion"]} is not supported (expected {SchemaVersion})");
        }

        if (json.Value<string>("kind") != Kind)
        {
            problems.Add($"{entry}: kind: '{json["kind"]}' is not an editor package ({Kind}); content bundles are loaded by games, not imported");
        }

        var name = json.Value<string>("name");
        if (!IsValidName(name))
        {
            problems.Add($"{entry}: name: letters, digits, '.', '_' and '-', up to 64, starting with a letter");
        }

        var version = json.Value<string>("version");
        if (version is not { Length: > 0 and <= 32 })
        {
            problems.Add($"{entry}: version: 1 to 32 characters");
        }

        var description = json.Value<string?>("description");
        if (description is { Length: > 1024 })
        {
            problems.Add($"{entry}: description: longer than 1024 characters");
        }

        return problems.Count == before ? new EditorPackageManifest(name!, version!, description) : null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>One file an import writes: where it goes (relative to the project) and whether it replaces a file the package did not put there.</summary>
public sealed record EditorPackageImportFile(EditorPackageEntry Entry, string Target, bool ReplacesOtherFile);

/// <summary>What importing a package into a project would do, for the person to confirm; nothing has changed yet.</summary>
/// <param name="InstalledVersion">The version of this package the project has, if any (the import upgrades it).</param>
/// <param name="Removed">Files an earlier version installed that this one no longer has (deleted on import).</param>
/// <param name="CreatesScriptsProject">The project has no <c>scripts.project</c>; importing creates one (as the Basic templates have) for the package's code.</param>
/// <param name="CreatesEditorProject">The project has no <c>scripts.editorProject</c>; importing creates one for the package's editor code.</param>
public sealed record EditorPackageImportPlan(
    EditorPackageManifest Manifest,
    string? InstalledVersion,
    IReadOnlyList<EditorPackageImportFile> Files,
    IReadOnlyList<string> Removed,
    bool CreatesScriptsProject,
    bool CreatesEditorProject,
    bool HasCode,
    IReadOnlyList<string> Problems)
{
    public bool CanImport => Problems.Count == 0;
}

/// <summary>
/// Imports editor packages into a project: assets into <c>Assets/&lt;Name&gt;/</c>, script sources next to the game's
/// C# project, editor sources next to the editor project (both created when the project has none), and a receipt in
/// <c>ProjectSettings/Packages/&lt;Name&gt;.json</c> listing what was installed, so a newer version replaces it and
/// removes what it no longer has. The editor picks the files up as usual: assets refresh, the code recompiles.
/// </summary>
public static class EditorPackageImporter
{
    public const string ReceiptFolder = "ProjectSettings/Packages";

    /// <summary>What <see cref="Import"/> would do, with every reason it cannot.</summary>
    public static EditorPackageImportPlan Plan(GameProject project, EditorPackageFile package)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(package);
        var problems = new List<string>();
        var receipt = ReadReceipt(project, package.Manifest.Name, problems);
        var installed = new HashSet<string>(receipt?.Files ?? [], StringComparer.OrdinalIgnoreCase);
        var hasCode = package.Entries.Any(e => e.Area is EditorPackageFile.ScriptsArea or EditorPackageFile.EditorArea);
        var createsScriptsProject = hasCode && project.Scripts?.Project is not { Length: > 0 };
        var scriptsDirectory = project.Scripts?.Project is { Length: > 0 } scripts
            ? System.IO.Path.GetDirectoryName(project.ResolveProjectFile(scripts))!
            : System.IO.Path.Combine(project.RootDirectory, EditorPackageFile.ScriptsArea);
        var createsEditorProject = package.Entries.Any(e => e.Area == EditorPackageFile.EditorArea) && project.Scripts?.EditorProject is not { Length: > 0 };
        var editorDirectory = project.Scripts?.EditorProject is { Length: > 0 } editor
            ? System.IO.Path.GetDirectoryName(project.ResolveProjectFile(editor))!
            : System.IO.Path.Combine(project.RootDirectory, EditorPackageFile.EditorArea);

        var files = new List<EditorPackageImportFile>();
        foreach (var entry in package.Entries)
        {
            var root = entry.Area switch
            {
                EditorPackageFile.AssetsArea => project.AssetsDirectory,
                EditorPackageFile.ScriptsArea => scriptsDirectory,
                _ => editorDirectory,
            };
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, entry.Path));
            if (!full.StartsWith(System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{entry.Area}/{entry.Path}: would be written outside the project");
                continue;
            }

            var target = System.IO.Path.GetRelativePath(project.RootDirectory, full).Replace('\\', '/');
            files.Add(new EditorPackageImportFile(entry, target, File.Exists(full) && !installed.Contains(target)));
        }

        CheckGuids(project, package, files, problems);
        var targets = new HashSet<string>(files.Select(f => f.Target), StringComparer.OrdinalIgnoreCase);
        var removed = installed.Where(f => !targets.Contains(f)).Order(StringComparer.Ordinal).ToList();
        return new EditorPackageImportPlan(package.Manifest, receipt?.Version, files, removed, createsScriptsProject, createsEditorProject,
            package.Entries.Any(e => e.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)), problems);
    }

    /// <summary>Imports the package as <see cref="Plan"/> describes: writes its files, removes what an earlier version left, creates the C# projects it needs and writes the receipt.</summary>
    /// <param name="engineRoot">The engine created projects compile against (<see cref="EngineLocation.FindRoot"/>).</param>
    /// <returns>The plan that was carried out.</returns>
    /// <exception cref="EditorPackageException">The plan has problems; nothing was changed.</exception>
    /// <exception cref="IOException">A file could not be written (files written before it stay).</exception>
    public static EditorPackageImportPlan Import(GameProject project, EditorPackageFile package, string engineRoot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(package);
        var plan = Plan(project, package);
        if (!plan.CanImport)
        {
            throw new EditorPackageException(package.Manifest.Name, plan.Problems);
        }

        foreach (var file in plan.Files)
        {
            var full = System.IO.Path.Combine(project.RootDirectory, file.Target);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, package.Read(file.Entry));
        }

        foreach (var stale in plan.Removed)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(project.RootDirectory, stale));
            if (full.StartsWith(System.IO.Path.GetFullPath(project.RootDirectory), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
            {
                File.Delete(full);
            }
        }

        if (plan.CreatesScriptsProject)
        {
            CreateScriptsProject(project, engineRoot);
        }

        if (plan.CreatesEditorProject)
        {
            CreateEditorProject(project, engineRoot);
        }

        var receipt = System.IO.Path.Combine(project.RootDirectory, ReceiptFolder, package.Manifest.Name + ".json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(receipt)!);
        File.WriteAllText(receipt, new JObject
        {
            ["schemaVersion"] = 1,
            ["name"] = package.Manifest.Name,
            ["version"] = package.Manifest.Version,
            ["files"] = new JArray(plan.Files.Select(f => f.Target).Order(StringComparer.Ordinal)),
        }.ToString(Formatting.Indented));
        return plan;
    }

    /// <summary>
    /// The scripts project an import creates, as the Basic templates' (<c>templates/Basic2D/Scripts</c>): the engine's
    /// gameplay API from its source tree, or the MNVTEngine.Sdk package of an installed engine. Set as <c>scripts.project</c>.
    /// </summary>
    private static void CreateScriptsProject(GameProject project, string engineRoot)
    {
        var file = System.IO.Path.Combine(project.RootDirectory, EditorPackageFile.ScriptsArea, Identifier(project) + ".Scripts.csproj");
        var root = System.IO.Path.GetFullPath(engineRoot).Replace('\\', '/').TrimEnd('/');
        var world = project.Profile == Modules.ProjectProfile.ThreeD ? "World3D" : "World2D";
        var references = EngineLocation.InstalledVersion(engineRoot) is { } installed
            ? $"""
                <PropertyGroup><RestoreAdditionalProjectSources>{root}/Engine/Packages</RestoreAdditionalProjectSources></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="MNVTEngine.Sdk" Version="{installed.PackageVersion}" ExcludeAssets="runtime" />
                  </ItemGroup>
                """
            : $"""
                <ItemGroup>
                    <ProjectReference Include="{root}/src/Engine/MNVTEngine.Scenes/MNVTEngine.Scenes.csproj" Private="false" />
                    <ProjectReference Include="{root}/src/Engine/MNVTEngine.{world}/MNVTEngine.{world}.csproj" Private="false" />
                    <ProjectReference Include="{root}/src/Engine/MNVTEngine.UI/MNVTEngine.UI.csproj" Private="false" />
                  </ItemGroup>
                """;
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- The game's C# components. Compiled by the editor, `mnvt run` and `mnvt build`; references the engine's gameplay API only. -->
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  {references}
                </Project>

                """);
        }

        project.Scripts ??= new ScriptSettings();
        project.Scripts.Project = SetScriptSetting(project, "project", file);
    }

    /// <summary>The editor project an import creates: it references the game scripts and the editor extension API, and is set as <c>scripts.editorProject</c>.</summary>
    private static void CreateEditorProject(GameProject project, string engineRoot)
    {
        var scripts = project.ResolveProjectFile(project.Scripts!.Project!);
        var file = System.IO.Path.Combine(project.RootDirectory, EditorPackageFile.EditorArea, Identifier(project) + ".Editor.csproj");
        var extensions = EngineLocation.IsInstalled(engineRoot)
            ? $"""<Reference Include="{System.IO.Path.Combine(engineRoot, "Editor", "MNVTEngine.Editor.Extensions.dll")}" Private="false" />"""
            : $"""<ProjectReference Include="{System.IO.Path.Combine(engineRoot, "src", "Editor", "MNVTEngine.Editor.Extensions", "MNVTEngine.Editor.Extensions.csproj")}" Private="false" />""";
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- The project's editor code (docs/editor/extensions.md): windows, Tools menu items and inspector sections.
                       Built and loaded by the editor with the game scripts; never part of a game build. -->
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(file)!, scripts).Replace('\\', '/')}" />
                    {extensions}
                  </ItemGroup>
                </Project>

                """);
        }

        project.Scripts.EditorProject = SetScriptSetting(project, "editorProject", file);
    }

    /// <summary>Sets <c>scripts.&lt;name&gt;</c> in the project file to <paramref name="file"/>, keeping everything else; the new file is swapped in whole.</summary>
    private static string SetScriptSetting(GameProject project, string name, string file)
    {
        var relative = System.IO.Path.GetRelativePath(project.RootDirectory, file).Replace('\\', '/');
        var document = JObject.Parse(File.ReadAllText(project.ProjectFile));
        var settings = document["scripts"] as JObject ?? [];
        settings[name] = relative;
        document["scripts"] = settings;
        var temporary = project.ProjectFile + ".tmp";
        File.WriteAllText(temporary, document.ToString(Formatting.Indented) + Environment.NewLine);
        File.Move(temporary, project.ProjectFile, overwrite: true);
        return relative;
    }

    /// <summary>A C# name for the project's created projects: its file name, with anything but letters, digits, '_' and '.' replaced.</summary>
    private static string Identifier(GameProject project)
    {
        var name = new string([.. System.IO.Path.GetFileNameWithoutExtension(project.ProjectFile).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' ? c : '_')]);
        return name.Length > 0 && char.IsAsciiLetter(name[0]) ? name : "Game" + name;
    }

    /// <summary>A package's asset GUIDs must not belong to other assets of the project: the database would reassign one and break references to it.</summary>
    private static void CheckGuids(GameProject project, EditorPackageFile package, List<EditorPackageImportFile> files, List<string> problems)
    {
        var incoming = new Dictionary<Guid, string>();
        foreach (var file in files.Where(f => f.Entry.Area == EditorPackageFile.AssetsArea && f.Target.EndsWith(Assets.AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase)))
        {
            if (Guid.TryParse(ReadGuid(package.Read(file.Entry)), out var guid) && !incoming.TryAdd(guid, file.Target))
            {
                problems.Add($"{file.Target}: GUID {guid} is used twice in the package");
            }
        }

        if (incoming.Count == 0 || !Directory.Exists(project.AssetsDirectory))
        {
            return;
        }

        foreach (var meta in Directory.EnumerateFiles(project.AssetsDirectory, "*" + Assets.AssetDatabase.MetaExtension, SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(project.RootDirectory, meta).Replace('\\', '/');
            if (Guid.TryParse(ReadGuid(File.ReadAllBytes(meta)), out var guid) && incoming.TryGetValue(guid, out var target)
                && !string.Equals(target, relative, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{target}: GUID {guid} already belongs to {relative}");
            }
        }
    }

    private static string? ReadGuid(byte[] meta)
    {
        try
        {
            return JObject.Parse(Encoding.UTF8.GetString(meta)).Value<string>("guid");
        }
        catch (JsonException)
        {
            return null; // not readable: the asset database reports it when it imports the file
        }
    }

    private static (string Version, IReadOnlyList<string> Files)? ReadReceipt(GameProject project, string name, List<string> problems)
    {
        var file = System.IO.Path.Combine(project.RootDirectory, ReceiptFolder, name + ".json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var json = JObject.Parse(File.ReadAllText(file));
            var files = (json["files"] as JArray ?? []).Select(f => f.Value<string>() ?? "").Where(f => f.Length > 0 && !f.Contains("..", StringComparison.Ordinal)).ToList();
            return (json.Value<string>("version") ?? "?", files);
        }
        catch (JsonException ex)
        {
            problems.Add($"{ReceiptFolder}/{name}.json: {ex.Message} (fix or delete it to import again)");
            return null;
        }
    }

    /// <summary>For messages: a size the way people read it.</summary>
    public static string Size(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}
