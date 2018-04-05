﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.AspNetCore.Blazor.Build
{
    internal class EmbeddedResourcesProcessor
    {
        const string ContentSubdirName = "_content";
        const string JsFileLogicalNamePrefix = "blazor:js:";
        const string CssFileLogicalNamePrefix = "blazor:css:";
        const string StaticFileLogicalNamePrefix = "blazor:file:";

        /// <summary>
        /// Finds Blazor-specific embedded resources in the specified assemblies, writes them
        /// to disk, and returns a description of those resources in dependency order.
        /// </summary>
        /// <param name="entrypointAssemblyPath">The path to the application startup assembly.</param>
        /// <param name="referencedAssemblyPaths">The paths to assemblies that may contain embedded resources.</param>
        /// <param name="outputDir">The path to the directory where output is being written.</param>
        /// <returns>A description of the embedded resources that were written to disk.</returns>
        public static IReadOnlyList<EmbeddedResourceInfo> ExtractEmbeddedResources(
            string entrypointAssemblyPath, IEnumerable<string> referencedAssemblyPaths, string outputDir)
        {
            // Clean away any earlier state
            var contentDir = Path.Combine(outputDir, ContentSubdirName);
            if (Directory.Exists(contentDir))
            {
                Directory.Delete(contentDir, recursive: true);
            }

            // First, get an ordered list of AssemblyDefinition instances
            var entrypointDefinition = AssemblyDefinition.ReadAssembly(entrypointAssemblyPath);
            var referencedAssemblyDefinitions = referencedAssemblyPaths
                .Where(path => !Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)) // Skip System.* because they are never going to contain embedded resources that we want
                .Select(path => AssemblyDefinition.ReadAssembly(path))
                .ToList();
            referencedAssemblyDefinitions.Sort(OrderWithReferenceSubjectFirst);

            // Now process them in turn
            return referencedAssemblyDefinitions
                .SelectMany(def => ExtractEmbeddedResourcesFromSingleAssembly(def, outputDir))
                .ToList()
                .AsReadOnly();
        }

        private static IEnumerable<EmbeddedResourceInfo> ExtractEmbeddedResourcesFromSingleAssembly(
            AssemblyDefinition assemblyDefinition, string outputDirPath)
        {
            var assemblyName = assemblyDefinition.Name.Name;
            foreach (var res in assemblyDefinition.MainModule.Resources)
            {
                if (TryExtractEmbeddedResource(assemblyName, res, outputDirPath, out var extractedResourceInfo))
                {
                    yield return extractedResourceInfo;
                }
            }
        }

        private static bool TryExtractEmbeddedResource(string assemblyName, Resource resource, string outputDirPath, out EmbeddedResourceInfo extractedResourceInfo)
        {
            if (resource is EmbeddedResource embeddedResource)
            {
                if (TryInterpretLogicalName(resource.Name, out var kind, out var name))
                {
                    // Prefix the output path with the assembly name to ensure no clashes
                    // Also be invariant to the OS on which the package was built
                    name = Path.Combine(ContentSubdirName, assemblyName, EnsureHasPathSeparators(name, Path.DirectorySeparatorChar));

                    // Write the file content to disk, ensuring we don't try to write outside the output root
                    var outputPath = Path.GetFullPath(Path.Combine(outputDirPath, name));
                    if (!outputPath.StartsWith(outputDirPath))
                    {
                        throw new InvalidOperationException($"Cannot write embedded resource from assembly '{assemblyName}' to '{outputPath}' because it is outside the expected directory {outputDirPath}");
                    }
                    WriteResourceFile(embeddedResource, outputPath);

                    // The URLs we write into the index.html file need to use web-style directory separators
                    extractedResourceInfo = new EmbeddedResourceInfo(kind, EnsureHasPathSeparators(name, '/'));
                    return true;
                }
            }

            extractedResourceInfo = null;
            return false;
        }

        private static void WriteResourceFile(EmbeddedResource resource, string outputPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            using (var outputStream = File.OpenWrite(outputPath))
            {
                resource.GetResourceStream().CopyTo(outputStream);
            }
        }

        private static string EnsureHasPathSeparators(string name, char desiredSeparatorChar) => name
            .Replace('\\', desiredSeparatorChar)
            .Replace('/', desiredSeparatorChar);

        private static bool TryInterpretLogicalName(string logicalName, out EmbeddedResourceKind kind, out string resolvedName)
        {
            if (logicalName.StartsWith(JsFileLogicalNamePrefix, StringComparison.Ordinal))
            {
                kind = EmbeddedResourceKind.JavaScript;
                resolvedName = logicalName.Substring(JsFileLogicalNamePrefix.Length);
                return true;
            }

            if (logicalName.StartsWith(CssFileLogicalNamePrefix, StringComparison.Ordinal))
            {
                kind = EmbeddedResourceKind.Css;
                resolvedName = logicalName.Substring(CssFileLogicalNamePrefix.Length);
                return true;
            }

            if (logicalName.StartsWith(StaticFileLogicalNamePrefix, StringComparison.Ordinal))
            {
                kind = EmbeddedResourceKind.Static;
                resolvedName = logicalName.Substring(StaticFileLogicalNamePrefix.Length);
                return true;
            }

            kind = default;
            resolvedName = default;
            return false;
        }

        // For each assembly B that references A, we want the resources from A to be loaded before
        // the references for B (because B's resources might depend on A's resources)
        private static int OrderWithReferenceSubjectFirst(AssemblyDefinition a, AssemblyDefinition b)
            => AssemblyHasReference(a, b) ? 1
            : AssemblyHasReference(b, a) ? -1
            : 0;

        private static bool AssemblyHasReference(AssemblyDefinition from, AssemblyDefinition to)
            => from.MainModule.AssemblyReferences
                .Select(reference => reference.Name)
                .Contains(to.Name.Name, StringComparer.Ordinal);
    }
}