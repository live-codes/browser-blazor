using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

/// <summary>
/// The compiler's bulk payloads — the BCL + ASP.NET Core reference assemblies and the Razor compiler
/// — are fetched on first use rather than embedded in this assembly.
///
/// Embedded, they put ~16 MB into the app assembly, and the browser cannot start the app without
/// downloading the whole thing. As separate payloads the runtime boots against a small assembly, so
/// the page paints sooner; a console-only session never fetches the Razor compiler at all; and a
/// rebuild of this app no longer invalidates ~16 MB of reference assemblies in the HTTP cache, since
/// the payloads keep their own URLs.
///
/// Each payload is one zip in the bundle, fetched from the bundle's base URL through the browser's
/// own fetch (so the HTTP cache and CORS rules apply as they do to every other asset).
/// </summary>
public static class Payload
{
    /// <summary>The reference assemblies the in-page compiler compiles against.</summary>
    public const string References = "refs.zip";

    /// <summary>The Razor source generator, its utilities assembly, and nothing else.</summary>
    public const string RazorCompiler = "razor.zip";

    /// <summary>The bundle's base URL, pushed in by blazor-wasm.js. It is the bundle's, not the
    /// page's: the two need not share an origin.</summary>
    public static string BaseUrl = "";

    static HttpClient _http;

    /// <summary>The client payloads are fetched with. Program supplies the one the runtime
    /// registered once the host is built, but the first interop call can arrive before that line
    /// runs, so one is created on demand until then — the runtime routes either through the
    /// browser's fetch.</summary>
    public static HttpClient Http
    {
        get => _http ??= new HttpClient();
        set => _http = value;
    }

    static readonly object Gate = new object();
    static readonly Dictionary<string, Task<Dictionary<string, byte[]>>> InFlight =
        new Dictionary<string, Task<Dictionary<string, byte[]>>>(StringComparer.Ordinal);

    /// <summary>The zip's entries by file name. The first caller fetches; later ones share the task,
    /// so a payload is fetched once no matter how many compiles are waiting on it.</summary>
    public static Task<Dictionary<string, byte[]>> GetAsync(string fileName)
    {
        lock (Gate)
        {
            if (InFlight.TryGetValue(fileName, out var existing))
            {
                return existing;
            }

            var task = FetchAsync(fileName);
            InFlight[fileName] = task;
            return task;
        }
    }

    static async Task<Dictionary<string, byte[]>> FetchAsync(string fileName)
    {
        if (Http is null)
        {
            throw new InvalidOperationException("The host has not started, so there is no HttpClient.");
        }

        if (string.IsNullOrEmpty(BaseUrl))
        {
            throw new InvalidOperationException(
                "The bundle's base URL was not supplied, so '" + fileName + "' cannot be located.");
        }

        var bytes = await Http.GetByteArrayAsync(BaseUrl + fileName);

        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length);
            entryStream.CopyTo(buffer);
            entries[entry.FullName] = buffer.ToArray();
        }

        return entries;
    }
}
