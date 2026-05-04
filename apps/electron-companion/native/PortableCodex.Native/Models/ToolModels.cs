using System.Text.Json.Nodes;

namespace PortableCodex.Native.Models;

public sealed class PatchOperation
{
    public string Find { get; set; } = string.Empty;

    public string Replace { get; set; } = string.Empty;

    public int? Occurrence { get; set; }

    public bool? ReplaceAll { get; set; }
}

public sealed class ToolRequest
{
    public string RequestId { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string Tool { get; set; } = string.Empty;

    public string? SkillName { get; set; }

    public string? WorkspaceRoot { get; set; }

    public string? Path { get; set; }

    public string? Content { get; set; }

    public bool? CreateDirectories { get; set; }

    public List<PatchOperation>? Operations { get; set; }

    public string? Patch { get; set; }

    public string? Query { get; set; }

    public bool? IsRegex { get; set; }

    public bool? CaseSensitive { get; set; }

    public int? MaxResults { get; set; }

    public List<string>? FileExtensions { get; set; }

    public bool? Multithreaded { get; set; }

    public int? MaxSearchThreads { get; set; }

    public bool? Recursive { get; set; }

    public string? Command { get; set; }

    public string? Shell { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? TimeoutMs { get; set; }

    public int? MaxOutputBytes { get; set; }

    public string? Encoding { get; set; }

    public int? MaxBytes { get; set; }

    public string? Prompt { get; set; }

    public string? Title { get; set; }
}

public sealed class ToolResponse
{
    public string RequestId { get; set; } = string.Empty;

    public string Status { get; set; } = "error";

    public JsonNode? Result { get; set; }

    public ToolError? Error { get; set; }

    public ApprovalRequired? ApprovalRequired { get; set; }
}

public sealed class ToolError
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public sealed class ApprovalRequired
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class DirectoryEntry
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Kind { get; set; } = "file";

    public long Size { get; set; }
}

public sealed class SearchMatch
{
    public string Path { get; set; } = string.Empty;

    public int Line { get; set; }

    public int Column { get; set; }

    public string Preview { get; set; } = string.Empty;
}

public sealed class PathStatResult
{
    public string Path { get; set; } = string.Empty;

    public string Kind { get; set; } = "file";

    public long Size { get; set; }

    public string ModifiedAt { get; set; } = string.Empty;
}

public sealed class ToolAuditEntry
{
    public string RequestId { get; set; } = string.Empty;

    public string Tool { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string? WorkspaceRoot { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string? CompletedAt { get; set; }

    public string Status { get; set; } = "pending";

    public string ArgumentsSummary { get; set; } = string.Empty;
}

public sealed class ApiPrincipal
{
    public string UserId { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string? DefaultDeviceId { get; set; }
}

public sealed class RelayConfig
{
    public int Port { get; set; }

    public int RequestTimeoutMs { get; set; } = 30_000;

    public List<ApiPrincipal> ApiPrincipals { get; set; } = [];

    public Dictionary<string, string> DeviceTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
