using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

/// <summary>
/// The reference assemblies the in-page Roslyn compiler compiles against: the BCL plus ASP.NET Core
/// (Blazor). They travel as the "refs.zip" payload next to the app rather than inside it, so the
/// runtime can start without them and the browser can cache them across app rebuilds. Shared by both
/// the component compiler and the console runner.
/// </summary>
public static class ReferenceAssemblies
{
    static readonly object Gate = new object();
    static readonly List<MetadataReference> Items = new List<MetadataReference>();

    static Task _loading;

    /// <summary>How many are loaded — 0 until the first compile fetches them.</summary>
    public static int Count => Items.Count;

    public static IReadOnlyList<MetadataReference> All => Items;

    /// <summary>Fetches the payload and turns it into metadata references, once. Every compile waits
    /// on this; only the first one pays for the download.</summary>
    public static Task EnsureLoadedAsync()
    {
        lock (Gate)
        {
            return _loading ??= LoadAsync();
        }
    }

    static async Task LoadAsync()
    {
        var entries = await Payload.GetAsync(Payload.References);

        // Sorted so the reference set is the same every run, whatever order the zip stored.
        var names = new List<string>(entries.Keys);
        names.Sort(StringComparer.Ordinal);

        foreach (var name in names)
        {
            Items.Add(MetadataReference.CreateFromImage(entries[name], filePath: name));
        }
    }
}
