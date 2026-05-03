using System.IO;

namespace PortableCodex.Native.Services;

public sealed class PathPolicyService
{
    public string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        return Canonicalize(workspaceRoot);
    }

    public bool IsWorkspaceTrusted(IEnumerable<string> trustedRoots, string workspaceRoot)
    {
        var root = NormalizeComparisonValue(NormalizeWorkspaceRoot(workspaceRoot));
        return trustedRoots.Any(trusted => NormalizeComparisonValue(NormalizeWorkspaceRoot(trusted)) == root);
    }

    public string ResolvePathWithinWorkspace(string workspaceRoot, string targetPath = ".")
    {
        var canonicalRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var absoluteTarget = Canonicalize(Path.GetFullPath(targetPath, canonicalRoot));
        var relative = Path.GetRelativePath(canonicalRoot, absoluteTarget);
        var escaped =
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative);

        if (escaped)
        {
            throw new InvalidOperationException($"Path escapes workspace root: {targetPath}");
        }

        return absoluteTarget;
    }

    public string ToRelativeWorkspacePath(string workspaceRoot, string absolutePath)
    {
        var relative = Path.GetRelativePath(NormalizeWorkspaceRoot(workspaceRoot), absolutePath);
        return string.IsNullOrWhiteSpace(relative) ? "." : relative;
    }

    private static string Canonicalize(string inputPath)
    {
        var resolved = Path.GetFullPath(inputPath);
        if (Directory.Exists(resolved))
        {
            return Path.TrimEndingDirectorySeparator(new DirectoryInfo(resolved).FullName);
        }

        if (File.Exists(resolved))
        {
            return new FileInfo(resolved).FullName;
        }

        return resolved;
    }

    private static string NormalizeComparisonValue(string value)
    {
        return OperatingSystem.IsWindows() ? value.ToLowerInvariant() : value;
    }
}
