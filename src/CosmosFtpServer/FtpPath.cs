// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System.Collections.Generic;

namespace CosmosFtpServer;

/// <summary>
/// The paths a client sees: rooted at <c>/</c>, which is the server's root
/// directory, and resolved without touching the file system, so no
/// <c>..</c> leads above the root.
/// </summary>
internal static class FtpPath
{
    /// <summary>The root as the client sees it.</summary>
    public const string Root = "/";

    /// <summary>
    /// Resolves what a client typed against its working directory: an
    /// absolute path starts from the root, <c>.</c> is dropped and <c>..</c>
    /// goes up a level, stopping at the root.
    /// </summary>
    /// <param name="workingDirectory">The client's working directory, as this method returns them.</param>
    /// <param name="path">The path the client sent.</param>
    /// <returns>The resolved path: rooted, with no trailing separator except the root's.</returns>
    public static string Resolve(string workingDirectory, string path)
    {
        List<string> segments = [];
        if (!path.StartsWith('/'))
        {
            Push(segments, workingDirectory);
        }

        Push(segments, path);
        return segments.Count == 0 ? Root : $"/{string.Join('/', segments)}";
    }

    /// <summary>Maps a resolved path to the file system, under <paramref name="rootDirectory"/>.</summary>
    public static string ToPhysical(string rootDirectory, string path)
    {
        string root = rootDirectory.TrimEnd('/');
        if (path == Root)
        {
            return root.Length == 0 ? Root : root;
        }

        return $"{root}{path}";
    }

    /// <summary>The last segment of a resolved path, the root's being empty.</summary>
    public static string GetName(string path) => path.Substring(path.LastIndexOf('/') + 1);

    private static void Push(List<string> segments, string path)
    {
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }
    }
}
