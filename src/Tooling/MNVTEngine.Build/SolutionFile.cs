// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Writes a Visual Studio solution (<c>.sln</c>) for a game project, so it opens from a double-click with the game's
/// C# scripts project loaded. The solution references the scripts project the <c>.mnvtproj</c> names; a project with
/// no scripts gets an empty solution. GUIDs are derived from the names, so re-running produces the same file.
/// </summary>
public static class SolutionFile
{
    private const string CSharpProjectType = "9A19103F-16F7-4668-BE54-9A1E7A4F7556"; // SDK-style C# project

    /// <summary>
    /// Writes <c>&lt;name&gt;.sln</c> into <paramref name="projectDirectory"/> for the project file there, and returns its path.
    /// </summary>
    /// <exception cref="Serialization.SerializationException">The project file cannot be read.</exception>
    public static string Write(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectDirectory);
        var projectFile = Directory.EnumerateFiles(projectDirectory, "*" + GameProject.Extension).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new FileNotFoundException($"No {GameProject.Extension} project in {projectDirectory}.");
        var project = GameProject.Load(projectFile);
        var name = Path.GetFileNameWithoutExtension(projectFile);
        var solutionPath = Path.Combine(projectDirectory, name + ".sln");
        File.WriteAllText(solutionPath, Build(name, project.Scripts?.Project));
        return solutionPath;
    }

    /// <summary>The solution file text for a project named <paramref name="solutionName"/> and its scripts project (or none).</summary>
    public static string Build(string solutionName, string? scriptsProjectRelative)
    {
        var sln = new StringBuilder();
        sln.Append("﻿\r\n") // the BOM and blank first line Visual Studio writes
            .Append("Microsoft Visual Studio Solution File, Format Version 12.00\r\n")
            .Append("# Visual Studio Version 17\r\n");

        var solutionGuid = DeterministicGuid("sln:" + solutionName);
        if (scriptsProjectRelative is { Length: > 0 })
        {
            var scripts = scriptsProjectRelative.Replace('/', '\\');
            var scriptsName = Path.GetFileNameWithoutExtension(scripts);
            var projectGuid = DeterministicGuid("csproj:" + scripts);
            sln.Append(CultureInfo.InvariantCulture, $"Project(\"{{{CSharpProjectType}}}\") = \"{scriptsName}\", \"{scripts}\", \"{{{projectGuid}}}\"\r\n")
                .Append("EndProject\r\n")
                .Append("Global\r\n")
                .Append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n")
                .Append("\t\tDebug|Any CPU = Debug|Any CPU\r\n")
                .Append("\t\tRelease|Any CPU = Release|Any CPU\r\n")
                .Append("\tEndGlobalSection\r\n")
                .Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n")
                .Append(CultureInfo.InvariantCulture, $"\t\t{{{projectGuid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n")
                .Append(CultureInfo.InvariantCulture, $"\t\t{{{projectGuid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n")
                .Append(CultureInfo.InvariantCulture, $"\t\t{{{projectGuid}}}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n")
                .Append(CultureInfo.InvariantCulture, $"\t\t{{{projectGuid}}}.Release|Any CPU.Build.0 = Release|Any CPU\r\n")
                .Append("\tEndGlobalSection\r\n");
        }
        else
        {
            sln.Append("Global\r\n");
        }

        sln.Append("\tGlobalSection(SolutionProperties) = preSolution\r\n")
            .Append("\t\tHideSolutionNode = FALSE\r\n")
            .Append("\tEndGlobalSection\r\n")
            .Append("\tGlobalSection(ExtensibilityGlobals) = postSolution\r\n")
            .Append(CultureInfo.InvariantCulture, $"\t\tSolutionGuid = {{{solutionGuid}}}\r\n")
            .Append("\tEndGlobalSection\r\n")
            .Append("EndGlobal\r\n");
        return sln.ToString();
    }

    /// <summary>A stable upper-case GUID for a string, so the same project always gets the same ids.</summary>
    internal static string DeterministicGuid(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash[..16]).ToString().ToUpperInvariant();
    }
}
