using System.IO;
using System.Text.RegularExpressions;

namespace PortableCodex.Native.Services;

public sealed partial class CodexCliWorkspaceService
{
    public IReadOnlyList<string> GetWorkspaceRoots()
    {
        var roots = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var configPath in GetConfigPaths())
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                foreach (var root in ExtractProjectRoots(File.ReadAllLines(configPath)))
                {
                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        roots.Add(root);
                    }
                }
            }
            catch
            {
                // Best-effort import only. The companion should keep running even if Codex config is unreadable.
            }
        }

        return roots.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetSkillRoots()
    {
        var roots = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var codexHome in GetCodexHomePaths())
        {
            var skillsDir = Path.Combine(codexHome, "skills");
            if (!Directory.Exists(skillsDir))
            {
                continue;
            }

            try
            {
                foreach (var skillDir in Directory.EnumerateDirectories(skillsDir))
                {
                    if (File.Exists(Path.Combine(skillDir, "SKILL.md")))
                    {
                        roots.Add(skillDir);
                    }
                }
            }
            catch
            {
                // Best-effort import only. The companion should keep running even if Codex skills are unreadable.
            }
        }

        return roots.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> GetConfigPaths()
    {
        foreach (var codexHome in GetCodexHomePaths())
        {
            yield return Path.Combine(codexHome, "config.toml");
        }
    }

    private static IEnumerable<string> GetCodexHomePaths()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return codexHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".codex");
        }
    }

    private static IEnumerable<string> ExtractProjectRoots(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = CodexProjectTableRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var raw = match.Groups["doubleQuoted"].Success
                ? UnescapeTomlBasicString(match.Groups["doubleQuoted"].Value)
                : match.Groups["singleQuoted"].Value;

            if (Path.IsPathFullyQualified(raw))
            {
                yield return raw;
            }
        }
    }

    private static string UnescapeTomlBasicString(string value)
    {
        return value
            .Replace("\\\\", "\\")
            .Replace("\\\"", "\"")
            .Replace("\\/", "/")
            .Replace("\\b", "\b")
            .Replace("\\t", "\t")
            .Replace("\\n", "\n")
            .Replace("\\f", "\f")
            .Replace("\\r", "\r");
    }

    [GeneratedRegex("^\\s*\\[projects\\.(?:\"(?<doubleQuoted>(?:\\\\.|[^\"])*)\"|'(?<singleQuoted>[^']*)')\\]\\s*$", RegexOptions.Compiled)]
    private static partial Regex CodexProjectTableRegex();
}
