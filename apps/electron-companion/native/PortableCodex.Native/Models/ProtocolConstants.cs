namespace PortableCodex.Native.Models;

public static class ProtocolConstants
{
    public static readonly string[] ToolNames =
    [
        "list_trusted_workspaces",
        "list_dir",
        "read_file",
        "write_file",
        "apply_patch",
        "search_files",
        "stat_path",
        "make_dir",
        "delete_path",
        "run_command",
    ];

    public static readonly IReadOnlyDictionary<string, string> ToolRouteMap = new Dictionary<string, string>
    {
        ["list_trusted_workspaces"] = "/tools/list-trusted-workspaces",
        ["list_dir"] = "/tools/list-dir",
        ["read_file"] = "/tools/read-file",
        ["write_file"] = "/tools/write-file",
        ["apply_patch"] = "/tools/apply-patch",
        ["search_files"] = "/tools/search-files",
        ["stat_path"] = "/tools/stat-path",
        ["make_dir"] = "/tools/make-dir",
        ["delete_path"] = "/tools/delete-path",
        ["run_command"] = "/tools/run-command",
    };

    public static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "apply_patch",
        "delete_path",
        "run_command",
    };
}
