using System.Reflection;

namespace RepoShelf.Core;

/// <summary>Access to the embedded web UI files (from <repo>/public).</summary>
public static class UiAssets
{
    private const string Prefix = "RepoShelf.Ui.";
    private static readonly Assembly Asm = typeof(UiAssets).Assembly;

    private static readonly Lazy<Dictionary<string, string>> Names = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Asm.GetManifestResourceNames())
        {
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var path = name[Prefix.Length..].Replace('\\', '/');
                map[path] = name;
            }
        }
        return map;
    });

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".md"] = "text/markdown; charset=utf-8",
    };

    public static bool TryRead(string path, out byte[] content, out string contentType)
    {
        content = Array.Empty<byte>();
        contentType = "application/octet-stream";
        if (!Names.Value.TryGetValue(path, out var resource))
        {
            return false;
        }
        using var stream = Asm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        content = ms.ToArray();
        contentType = Mime.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";
        return true;
    }

    public static string? ReadText(string path)
    {
        if (!TryRead(path, out var content, out _))
        {
            return null;
        }
        return System.Text.Encoding.UTF8.GetString(content);
    }
}
