using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

/// <summary>
/// The reference assemblies the in-page Roslyn compiler compiles against: the BCL plus
/// ASP.NET Core (Blazor). They are embedded in this app's manifest resources, so nothing has
/// to be fetched and no file system is involved. Shared by both the component compiler and
/// the console runner.
/// </summary>
public static class ReferenceAssemblies
{
    static readonly List<MetadataReference> Items = new List<MetadataReference>();

    public static int Count => Items.Count;

    public static IReadOnlyList<MetadataReference> All => Items;

    /// <summary>Loads reference assemblies embedded as resources named
    /// "&lt;prefix&gt;&lt;file&gt;.dll" (WebAssembly has no file system).</summary>
    public static void LoadFromAssemblyResources(Assembly assembly, string prefix = "lib.")
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Items.Add(MetadataReference.CreateFromImage(buffer.ToArray(), filePath: name.Substring(prefix.Length)));
        }
    }
}
