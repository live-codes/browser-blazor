using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// A project's static assets: every file under <c>wwwroot/</c>, keyed by the path below it — which is
/// how a Blazor project serves them — as data URLs the page can point at.
/// </summary>
public static class ProjectAssets
{
    const string Root = "wwwroot/";

    public static Dictionary<string, string> Collect(SourceFile[] files)
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files ?? Array.Empty<SourceFile>())
        {
            var name = file?.Filename;
            if (string.IsNullOrEmpty(name)) continue;

            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ||
                normalized.Length <= Root.Length)
            {
                continue;
            }

            var content = file.Content ?? "";
            var key = normalized.Substring(Root.Length);

            // A data URL is taken as-is, so binary assets can be supplied already encoded.
            assets[key] = content.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? content
                : "data:" + ContentType(key) + ";base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        }

        return assets;
    }

    static string ContentType(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var extension = dot < 0 ? "" : fileName.Substring(dot).ToLowerInvariant();

        switch (extension)
        {
            case ".svg": return "image/svg+xml";
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".gif": return "image/gif";
            case ".webp": return "image/webp";
            case ".ico": return "image/x-icon";
            case ".css": return "text/css";
            case ".js": return "text/javascript";
            case ".json": return "application/json";
            case ".html": return "text/html";
            case ".txt": return "text/plain";
            case ".woff": return "font/woff";
            case ".woff2": return "font/woff2";
            case ".ttf": return "font/ttf";
            default: return "application/octet-stream";
        }
    }
}
