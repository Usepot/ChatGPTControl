namespace PortableCodex.Native.Models;

public static class ProtocolConstants
{
    public static readonly string[] ToolNames =
    [
        "list_trusted_workspaces",
        "get_gpt_instructions",
        "list_skills",
        "get_skill",
        "list_dir",
        "read_file",
        "write_file",
        "apply_patch",
        "search_files",
        "stat_path",
        "make_dir",
        "delete_path",
        "run_command",
        "shell",
        "exec_command",
        "shell_command",
        "write_stdin",
        "request_permissions",
        "view_image",
        "screenshot_desktop",
    ];

    public static readonly IReadOnlyDictionary<string, string> ToolRouteMap = new Dictionary<string, string>
    {
        ["list_trusted_workspaces"] = "/tools/list-trusted-workspaces",
        ["get_gpt_instructions"] = "/tools/get-gpt-instructions",
        ["list_skills"] = "/tools/list-skills",
        ["get_skill"] = "/tools/get-skill",
        ["list_dir"] = "/tools/list-dir",
        ["read_file"] = "/tools/read-file",
        ["write_file"] = "/tools/write-file",
        ["apply_patch"] = "/tools/apply-patch",
        ["search_files"] = "/tools/search-files",
        ["stat_path"] = "/tools/stat-path",
        ["make_dir"] = "/tools/make-dir",
        ["delete_path"] = "/tools/delete-path",
        ["run_command"] = "/tools/run-command",
        ["shell"] = "/tools/shell",
        ["exec_command"] = "/tools/exec-command",
        ["shell_command"] = "/tools/shell-command",
        ["write_stdin"] = "/tools/write-stdin",
        ["request_permissions"] = "/tools/request-permissions",
        ["view_image"] = "/tools/view-image",
        ["screenshot_desktop"] = "/tools/screenshot-desktop",
    };

    public static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "apply_patch",
        "delete_path",
        "run_command",
        "shell",
        "exec_command",
        "shell_command",
        "screenshot_desktop",
    };
}
