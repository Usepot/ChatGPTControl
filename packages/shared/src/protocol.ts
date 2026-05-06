export const TOOL_NAMES = [
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
] as const;

export type ToolName = (typeof TOOL_NAMES)[number];

export type ToolStatus = "ok" | "denied" | "error" | "timeout";

export interface ToolRequestEnvelope {
  requestId: string;
  deviceId?: string;
}

export interface ToolRequestBase extends ToolRequestEnvelope {
  workspaceRoot?: string;
}

export interface ListTrustedWorkspacesRequest extends ToolRequestEnvelope {
  tool: "list_trusted_workspaces";
}

export interface GetGptInstructionsRequest extends ToolRequestEnvelope {
  tool: "get_gpt_instructions";
}

export interface ListSkillsRequest extends ToolRequestEnvelope {
  tool: "list_skills";
}

export interface GetSkillRequest extends ToolRequestEnvelope {
  tool: "get_skill";
  skillName: string;
  maxBytes?: number;
}

export interface ListDirRequest extends ToolRequestBase {
  tool: "list_dir";
  path?: string;
}

export interface ReadFileRequest extends ToolRequestBase {
  tool: "read_file";
  path: string;
  encoding?: "utf-8" | "base64";
  maxBytes?: number;
}

export interface WriteFileRequest extends ToolRequestBase {
  tool: "write_file";
  path: string;
  content: string;
  createDirectories?: boolean;
}

export interface PatchOperation {
  find: string;
  replace: string;
  occurrence?: number;
  replaceAll?: boolean;
}

export interface ApplyPatchRequest extends ToolRequestBase {
  tool: "apply_patch";
  path?: string;
  operations?: PatchOperation[];
  patch?: string;
}

export interface SearchFilesRequest extends ToolRequestBase {
  tool: "search_files";
  path?: string;
  query: string;
  isRegex?: boolean;
  caseSensitive?: boolean;
  maxResults?: number;
  fileExtensions?: string[];
  multithreaded?: boolean;
  maxSearchThreads?: number;
}

export interface StatPathRequest extends ToolRequestBase {
  tool: "stat_path";
  path?: string;
}

export interface MakeDirRequest extends ToolRequestBase {
  tool: "make_dir";
  path: string;
}

export interface DeletePathRequest extends ToolRequestBase {
  tool: "delete_path";
  path: string;
  recursive?: boolean;
}

export interface RunCommandRequest extends ToolRequestBase {
  tool: "run_command";
  command: string;
  workdir?: string;
  workingDirectory?: string;
  timeoutMs?: number;
  maxOutputBytes?: number;
  stdin?: string;
}

export interface ShellRequest extends ToolRequestBase {
  tool: "shell" | "exec_command" | "shell_command";
  command?: string | string[];
  cmd?: string;
  commandLine?: string;
  workdir?: string;
  workingDirectory?: string;
  working_directory?: string;
  timeoutMs?: number;
  timeout_ms?: number;
  maxOutputBytes?: number;
  max_output_bytes?: number;
  yield_time_ms?: number;
  max_output_tokens?: number;
  tty?: boolean;
  shell?: string;
  login?: boolean;
  stdin?: string;
  input?: string;
}

export interface WriteStdinRequest extends ToolRequestEnvelope {
  tool: "write_stdin";
  processId?: string;
  sessionId?: string;
  session_id?: number;
  chars?: string;
  yield_time_ms?: number;
  max_output_tokens?: number;
  stdin?: string;
  input?: string;
}

export interface RequestPermissionsRequest extends ToolRequestEnvelope {
  tool: "request_permissions";
  permissions?: string[];
  reason?: string;
}

export interface ViewImageRequest extends ToolRequestBase {
  tool: "view_image";
  path: string;
  maxBytes?: number;
}

export interface ScreenshotDesktopRequest extends ToolRequestBase {
  tool: "screenshot_desktop";
  path?: string;
  screen?: "primary";
  maxBytes?: number;
}

export type ToolRequest =
  | ListTrustedWorkspacesRequest
  | GetGptInstructionsRequest
  | ListSkillsRequest
  | GetSkillRequest
  | ListDirRequest
  | ReadFileRequest
  | WriteFileRequest
  | ApplyPatchRequest
  | SearchFilesRequest
  | StatPathRequest
  | MakeDirRequest
  | DeletePathRequest
  | RunCommandRequest
  | ShellRequest
  | WriteStdinRequest
  | RequestPermissionsRequest
  | ViewImageRequest
  | ScreenshotDesktopRequest;

export interface DirectoryEntry {
  name: string;
  path: string;
  kind: "file" | "directory";
  size: number;
}

export interface SearchMatch {
  path: string;
  line: number;
  column: number;
  preview: string;
}

export interface PathStatResult {
  path: string;
  kind: "file" | "directory";
  size: number;
  modifiedAt: string;
}

export interface ToolResponse<Result = unknown> {
  requestId: string;
  status: ToolStatus;
  result?: Result;
  error?: {
    code: string;
    message: string;
  };
  approvalRequired?: {
    reason: string;
  };
}

export interface ToolAuditEntry {
  requestId: string;
  tool: ToolName;
  deviceId: string;
  workspaceRoot?: string;
  createdAt: string;
  completedAt?: string;
  status: ToolStatus | "pending";
  argumentsSummary: string;
}

export interface DeviceHelloMessage {
  type: "device:hello";
  deviceId: string;
  deviceName: string;
  token: string;
}

export interface RelayToolRequestMessage {
  type: "tool:request";
  request: ToolRequest;
}

export interface DeviceToolResponseMessage {
  type: "tool:response";
  response: ToolResponse;
}

export interface RelayPingMessage {
  type: "relay:ping";
  sentAt: string;
}

export interface DevicePongMessage {
  type: "device:pong";
  sentAt: string;
}

export type RelayToDeviceMessage = RelayToolRequestMessage | RelayPingMessage;

export type DeviceToRelayMessage =
  | DeviceHelloMessage
  | DeviceToolResponseMessage
  | DevicePongMessage;

export const TOOL_ROUTE_MAP: Record<ToolName, string> = {
  list_trusted_workspaces: "/tools/list-trusted-workspaces",
  get_gpt_instructions: "/tools/get-gpt-instructions",
  list_skills: "/tools/list-skills",
  get_skill: "/tools/get-skill",
  list_dir: "/tools/list-dir",
  read_file: "/tools/read-file",
  write_file: "/tools/write-file",
  apply_patch: "/tools/apply-patch",
  search_files: "/tools/search-files",
  stat_path: "/tools/stat-path",
  make_dir: "/tools/make-dir",
  delete_path: "/tools/delete-path",
  run_command: "/tools/run-command",
  shell: "/tools/shell",
  exec_command: "/tools/exec-command",
  shell_command: "/tools/shell-command",
  write_stdin: "/tools/write-stdin",
  request_permissions: "/tools/request-permissions",
  view_image: "/tools/view-image",
  screenshot_desktop: "/tools/screenshot-desktop",
};

export interface PairingConfig {
  relayUrl: string;
  deviceId: string;
  deviceToken: string;
  deviceName: string;
}

export interface CompanionSettings extends PairingConfig {
  gptApiToken: string;
  trustedWorkspaces: string[];
  skillRoots: string[];
  importCodexCliSkills: boolean;
  requireApprovalForWrites: boolean;
}

export interface ToolLogEntry {
  requestId: string;
  tool: ToolName;
  createdAt: string;
  completedAt?: string;
  workspaceRoot?: string;
  summary: string;
  status: "pending" | ToolStatus;
  approval: "not_required" | "approved" | "denied" | "pending";
  detail?: string;
}

export function isToolName(value: string): value is ToolName {
  return TOOL_NAMES.includes(value as ToolName);
}
