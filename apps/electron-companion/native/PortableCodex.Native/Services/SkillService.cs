using System.IO;
using System.Text;
using System.Text.Json;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class SkillService
{
    private const int DefaultSkillMaxBytes = 200_000;

    public ToolResponse ListSkills(ToolRequest request, CompanionSettings settings)
    {
        var skills = GetSkillList(settings)
            .Select(skill => new
            {
                name = skill.Name,
                description = skill.Description,
                path = skill.Path,
            })
            .ToList();

        return Ok(request.RequestId, new { skills });
    }

    public async Task<ToolResponse> GetSkillAsync(
        ToolRequest request,
        CompanionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var requestedName = NormalizeRequestedName(request.SkillName);
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return Error(request.RequestId, "SKILL_NAME_REQUIRED", "skillName is required");
        }

        var skill = GetSkillList(settings).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requestedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(candidate.Path), requestedName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return Error(request.RequestId, "SKILL_NOT_FOUND", $"Skill not found: {requestedName}");
        }

        var skillFile = Path.Combine(skill.Path, "SKILL.md");
        var bytes = await File.ReadAllBytesAsync(skillFile, cancellationToken);
        var limit = request.MaxBytes.GetValueOrDefault(DefaultSkillMaxBytes);
        if (limit <= 0)
        {
            limit = DefaultSkillMaxBytes;
        }

        var readLength = Math.Min(limit, bytes.Length);
        var instructions = Encoding.UTF8.GetString(bytes, 0, readLength);

        return Ok(
            request.RequestId,
            new
            {
                skill = new
                {
                    name = skill.Name,
                    description = skill.Description,
                    path = skill.Path,
                    instructions,
                    truncated = bytes.Length > limit,
                    bytes = readLength,
                },
            });
    }

    public IReadOnlyList<SkillListEntry> GetSkillList(CompanionSettings settings)
    {
        var skills = new List<SkillListEntry>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in settings.SkillRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var skillFile = Path.Combine(root, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            try
            {
                var metadata = ExtractMetadata(File.ReadAllText(skillFile));
                var fallbackName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var name = string.IsNullOrWhiteSpace(metadata.Name) ? fallbackName : metadata.Name;
                if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name))
                {
                    continue;
                }

                skills.Add(new SkillListEntry
                {
                    Name = name,
                    Description = metadata.Description,
                    Path = Path.GetFullPath(root),
                });
            }
            catch
            {
                // Ignore unreadable or malformed skills so one bad directory does not break all skills.
            }
        }

        return skills.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SkillMetadata ExtractMetadata(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new SkillMetadata(string.Empty, string.Empty, string.Empty);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return new SkillMetadata(string.Empty, string.Empty, string.Empty);
        }

        string name = string.Empty;
        string description = string.Empty;
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = UnquoteYamlScalar(line[(separator + 1)..].Trim());
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        return new SkillMetadata(name, description, string.Empty);
    }

    private static string NormalizeRequestedName(string? value)
    {
        return (value ?? string.Empty).Trim().TrimStart('/');
    }

    private static string UnquoteYamlScalar(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\'", "'", StringComparison.Ordinal);
        }

        return value;
    }

    private static ToolResponse Ok(string requestId, object result)
    {
        return new ToolResponse
        {
            RequestId = requestId,
            Status = "ok",
            Result = JsonSerializer.SerializeToNode(result, JsonDefaults.Transport),
        };
    }

    private static ToolResponse Error(string requestId, string code, string message)
    {
        return new ToolResponse
        {
            RequestId = requestId,
            Status = "error",
            Error = new ToolError
            {
                Code = code,
                Message = message,
            },
        };
    }

    private sealed record SkillMetadata(string Name, string Description, string Path);
}
