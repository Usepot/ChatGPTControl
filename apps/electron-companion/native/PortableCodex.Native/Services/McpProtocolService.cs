using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed class McpHttpResult
{
    public int StatusCode { get; init; }

    public JsonNode? Body { get; init; }
}

public static class McpProtocolService
{
    private const string ProtocolVersion = "2025-03-26";
    private const string WebSearchTool = "web_search";
    private static readonly HttpClient WebSearchHttpClient = new();

    public static async Task<McpHttpResult> HandleHttpBodyAsync(
        JsonNode? body,
        ApiPrincipal principal,
        string openApiJson,
        Func<ToolRequest, CancellationToken, Task<ToolResponse>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        if (body is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                return new McpHttpResult
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    Body = CreateErrorResponse(null, -32600, "Invalid JSON-RPC batch"),
                };
            }

            var responses = new JsonArray();
            foreach (var item in batch)
            {
                var response = await HandleJsonRpcMessageAsync(
                    item,
                    principal,
                    openApiJson,
                    dispatchAsync,
                    cancellationToken);
                if (response is not null)
                {
                    responses.Add(response);
                }
            }

            return responses.Count == 0
                ? new McpHttpResult { StatusCode = StatusCodes.Status202Accepted }
                : new McpHttpResult { StatusCode = StatusCodes.Status200OK, Body = responses };
        }

        var singleResponse = await HandleJsonRpcMessageAsync(
            body,
            principal,
            openApiJson,
            dispatchAsync,
            cancellationToken);

        if (singleResponse is null)
        {
            return new McpHttpResult { StatusCode = StatusCodes.Status202Accepted };
        }

        return new McpHttpResult
        {
            StatusCode = singleResponse["error"] is null
                ? StatusCodes.Status200OK
                : StatusCodes.Status400BadRequest,
            Body = singleResponse,
        };
    }

    private static async Task<JsonObject?> HandleJsonRpcMessageAsync(
        JsonNode? rawMessage,
        ApiPrincipal principal,
        string openApiJson,
        Func<ToolRequest, CancellationToken, Task<ToolResponse>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        if (rawMessage is not JsonObject message)
        {
            return CreateErrorResponse(null, -32600, "Invalid JSON-RPC request");
        }

        var id = NormalizeJsonRpcId(message);
        var expectsResponse = message.ContainsKey("id");
        var jsonrpc = GetString(message, "jsonrpc");
        var method = GetString(message, "method");

        if (!string.Equals(jsonrpc, "2.0", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(method))
        {
            return expectsResponse
                ? CreateErrorResponse(id, -32600, "Invalid JSON-RPC request")
                : null;
        }

        try
        {
            var result = await HandleJsonRpcMethodAsync(
                method,
                message["params"],
                principal,
                openApiJson,
                dispatchAsync,
                cancellationToken);

            return expectsResponse
                ? new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = result,
                }
                : null;
        }
        catch (McpMethodException ex)
        {
            return expectsResponse ? CreateErrorResponse(id, ex.Code, ex.Message, ex.DataNode) : null;
        }
        catch
        {
            return expectsResponse ? CreateErrorResponse(id, -32603, "Internal MCP server error") : null;
        }
    }

    private static async Task<JsonNode> HandleJsonRpcMethodAsync(
        string method,
        JsonNode? parameters,
        ApiPrincipal principal,
        string openApiJson,
        Func<ToolRequest, CancellationToken, Task<ToolResponse>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        return method switch
        {
            "initialize" => CreateInitializeResult(parameters),
            "ping" => new JsonObject(),
            "notifications/initialized" => new JsonObject(),
            "tools/list" => new JsonObject
            {
                ["tools"] = BuildMcpToolDescriptors(openApiJson),
            },
            "tools/call" => await CallToolAsync(parameters, principal, dispatchAsync, cancellationToken),
            "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
            "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
            _ => throw new McpMethodException(-32601, $"Method not found: {method}"),
        };
    }

    private static JsonObject CreateInitializeResult(JsonNode? parameters)
    {
        var requestedVersion = parameters is JsonObject obj
            ? GetString(obj, "protocolVersion")
            : null;

        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requestedVersion) ? ProtocolVersion : requestedVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "portable-codex",
                ["version"] = "0.1.0",
            },
            ["instructions"] = "Portable Codex exposes trusted local workspaces through MCP tools. Use web_search for current public web information when ChatGPT browsing is unavailable in app mode. When workspaceRoot is omitted, the paired companion uses its selected current workspace; pass workspaceRoot only to target another trusted root.",
        };
    }

    private static async Task<JsonObject> CallToolAsync(
        JsonNode? parameters,
        ApiPrincipal principal,
        Func<ToolRequest, CancellationToken, Task<ToolResponse>> dispatchAsync,
        CancellationToken cancellationToken)
    {
        if (parameters is not JsonObject parametersObject)
        {
            throw new McpMethodException(-32602, "tools/call requires params.name");
        }

        var tool = GetString(parametersObject, "name");
        if (string.Equals(tool, WebSearchTool, StringComparison.Ordinal))
        {
            var localArguments = parametersObject["arguments"] as JsonObject ?? new JsonObject();
            var localRequestId = GetString(localArguments, "requestId") ?? Guid.NewGuid().ToString();
            var localResponse = await RunWebSearchAsync(localRequestId, localArguments, cancellationToken);
            return CreateToolCallResult(localResponse);
        }

        if (string.IsNullOrWhiteSpace(tool) || !ProtocolConstants.ToolNames.Contains(tool, StringComparer.Ordinal))
        {
            throw new McpMethodException(-32602, $"Unknown tool: {tool}");
        }

        var arguments = parametersObject["arguments"] as JsonObject ?? new JsonObject();
        var requestId = GetString(arguments, "requestId") ?? Guid.NewGuid().ToString();
        var deviceId = GetString(arguments, "deviceId") ?? principal.DefaultDeviceId;

        var request = arguments.Deserialize<ToolRequest>(JsonDefaults.Transport) ?? new ToolRequest();
        request.Tool = tool;
        request.RequestId = requestId;
        request.DeviceId = deviceId;
        if (!ToolRequiresWorkspaceRoot(tool))
        {
            request.WorkspaceRoot = null;
        }

        var response = await dispatchAsync(request, cancellationToken);
        return CreateToolCallResult(response);
    }

    private static JsonObject CreateToolCallResult(ToolResponse response)
    {
        return new JsonObject
        {
            ["content"] = BuildToolContent(response),
            ["structuredContent"] = new JsonObject
            {
                ["requestId"] = response.RequestId,
                ["status"] = response.Status,
                ["result"] = SanitizeInlineImageData(response.Result),
                ["error"] = JsonSerializer.SerializeToNode(response.Error, JsonDefaults.Transport),
                ["approvalRequired"] = JsonSerializer.SerializeToNode(response.ApprovalRequired, JsonDefaults.Transport),
            },
            ["isError"] = !string.Equals(response.Status, "ok", StringComparison.Ordinal),
            ["_meta"] = new JsonObject
            {
                ["requestId"] = response.RequestId,
                ["status"] = response.Status,
            },
        };
    }

    private static JsonArray BuildToolContent(ToolResponse response)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = SummarizeToolResponse(response),
            },
        };

        if (string.Equals(response.Status, "ok", StringComparison.Ordinal) &&
            TryCreateImageContent(response.Result, out var imageContent))
        {
            content.Add(imageContent);
        }

        return content;
    }

    private static JsonArray BuildMcpToolDescriptors(string openApiJson)
    {
        var root = JsonNode.Parse(openApiJson) as JsonObject ?? new JsonObject();
        var paths = root["paths"] as JsonObject ?? new JsonObject();
        var schemas = root["components"]?["schemas"] as JsonObject ?? new JsonObject();
        var tools = new JsonArray();

        foreach (var (tool, route) in ProtocolConstants.ToolRouteMap)
        {
            var routePath = route.StartsWith("/", StringComparison.Ordinal) ? route : $"/{route}";
            var operation = paths[routePath]?["post"] as JsonObject ?? new JsonObject();
            var title = ToTitleCase(tool);
            tools.Add(
                new JsonObject
                {
                    ["name"] = tool,
                    ["title"] = title,
                    ["description"] = GetString(operation, "summary") ?? title,
                    ["inputSchema"] = GetOperationInputSchema(operation, schemas),
                    ["annotations"] = new JsonObject
                    {
                        ["readOnlyHint"] = true,
                        ["destructiveHint"] = false,
                        ["openWorldHint"] = false,
                    },
                    ["_meta"] = new JsonObject
                    {
                        ["openai/toolInvocation/invoking"] = $"Running {title}...",
                        ["openai/toolInvocation/invoked"] = $"{title} complete",
                    },
                });
        }

        tools.Add(BuildWebSearchToolDescriptor());

        return tools;
    }

    private static JsonObject BuildWebSearchToolDescriptor()
    {
        return new JsonObject
        {
            ["name"] = WebSearchTool,
            ["title"] = "Web Search",
            ["description"] = "Search the public web from the MCP server and return concise result titles, URLs, and snippets. Use for current information when ChatGPT web browsing is unavailable in app mode.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["requestId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional caller-generated request ID. The relay generates one if omitted.",
                    },
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Search query.",
                    },
                    ["maxResults"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum number of search results to return. Defaults to 5 and is clamped to 10.",
                        ["minimum"] = 1,
                        ["maximum"] = 10,
                        ["default"] = 5,
                    },
                },
                ["required"] = new JsonArray("query"),
                ["additionalProperties"] = true,
            },
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = true,
                ["destructiveHint"] = false,
                ["openWorldHint"] = false,
                ["idempotentHint"] = true,
            },
            ["_meta"] = new JsonObject
            {
                ["openai/toolInvocation/invoking"] = "Searching the web...",
                ["openai/toolInvocation/invoked"] = "Web search complete",
            },
        };
    }

    private static async Task<ToolResponse> RunWebSearchAsync(string requestId, JsonObject arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Status = "error",
                Error = new ToolError
                {
                    Code = "WEB_SEARCH_QUERY_REQUIRED",
                    Message = "web_search requires a non-empty query string.",
                },
            };
        }

        var maxResults = ClampInteger(arguments["maxResults"], 5, 1, 10);
        var requestUri = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            request.Headers.UserAgent.ParseAdd("PortableCodex/0.1");

            using var response = await WebSearchHttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ToolResponse
                {
                    RequestId = requestId,
                    Status = "error",
                    Error = new ToolError
                    {
                        Code = "WEB_SEARCH_HTTP_ERROR",
                        Message = $"Search provider returned HTTP {(int)response.StatusCode}.",
                    },
                };
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ToolResponse
            {
                RequestId = requestId,
                Status = "ok",
                Result = new JsonObject
                {
                    ["query"] = query,
                    ["provider"] = "DuckDuckGo HTML",
                    ["results"] = ParseDuckDuckGoResults(html, maxResults),
                },
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Status = "error",
                Error = new ToolError
                {
                    Code = "WEB_SEARCH_TIMEOUT",
                    Message = "Web search was canceled or timed out.",
                },
            };
        }
        catch (Exception ex)
        {
            return new ToolResponse
            {
                RequestId = requestId,
                Status = "error",
                Error = new ToolError
                {
                    Code = "WEB_SEARCH_FAILED",
                    Message = ex.Message,
                },
            };
        }
    }

    private static JsonArray ParseDuckDuckGoResults(string html, int maxResults)
    {
        var anchors = Regex.Matches(
            html,
            "<a\\b[^>]*class=[\"'][^\"']*result__a[^\"']*[\"'][^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var results = new JsonArray();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < anchors.Count && results.Count < maxResults; index++)
        {
            var anchor = anchors[index];
            var url = NormalizeDuckDuckGoResultUrl(WebUtility.HtmlDecode(anchor.Groups[1].Value));
            var title = StripHtml(anchor.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) || !seenUrls.Add(url))
            {
                continue;
            }

            var nextIndex = index + 1 < anchors.Count ? anchors[index + 1].Index : html.Length;
            var block = html[anchor.Index..nextIndex];
            var snippetMatch = Regex.Match(
                block,
                "class=[\"'][^\"']*result__snippet[^\"']*[\"'][^>]*>(.*?)</(?:a|div)>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var snippet = snippetMatch.Success ? StripHtml(snippetMatch.Groups[1].Value) : string.Empty;

            var result = new JsonObject
            {
                ["title"] = title,
                ["url"] = url,
            };
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                result["snippet"] = snippet;
            }

            results.Add(result);
        }

        return results;
    }

    private static string NormalizeDuckDuckGoResultUrl(string rawUrl)
    {
        try
        {
            var uri = new Uri(new Uri("https://duckduckgo.com"), rawUrl);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            return query.TryGetValue("uddg", out var redirected) && !string.IsNullOrWhiteSpace(redirected)
                ? redirected.ToString()
                : uri.ToString();
        }
        catch
        {
            return rawUrl;
        }
    }

    private static string StripHtml(string value)
    {
        var noTags = Regex.Replace(value, "<[^>]*>", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var decoded = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static int ClampInteger(JsonNode? node, int fallback, int minimum, int maximum)
    {
        var parsed = fallback;
        try
        {
            parsed = node?.GetValue<int>() ?? fallback;
        }
        catch
        {
            try
            {
                parsed = int.Parse(node?.GetValue<string>() ?? string.Empty, CultureInfo.InvariantCulture);
            }
            catch
            {
                parsed = fallback;
            }
        }

        return Math.Min(maximum, Math.Max(minimum, parsed));
    }

    private static JsonNode GetOperationInputSchema(JsonObject operation, JsonObject schemas)
    {
        var schema = operation["requestBody"]?["content"]?["application/json"]?["schema"];
        return schema is null
            ? new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = true,
            }
            : DereferenceJsonSchema(schema, schemas, []) ?? new JsonObject();
    }

    private static JsonNode? DereferenceJsonSchema(JsonNode? node, JsonObject schemas, HashSet<string> seenRefs)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(DereferenceJsonSchema(item, schemas, seenRefs));
                }

                return clone;
            }
            case JsonObject obj:
            {
                var refValue = GetString(obj, "$ref");
                if (!string.IsNullOrWhiteSpace(refValue) &&
                    refValue.StartsWith("#/components/schemas/", StringComparison.Ordinal) &&
                    !seenRefs.Contains(refValue))
                {
                    var schemaName = refValue["#/components/schemas/".Length..];
                    if (schemas[schemaName] is { } resolved)
                    {
                        var nextSeenRefs = new HashSet<string>(seenRefs, StringComparer.Ordinal) { refValue };
                        var dereferenced = DereferenceJsonSchema(resolved, schemas, nextSeenRefs);
                        var rest = CloneWithoutRef(obj, schemas, seenRefs);
                        if (rest.Count == 0)
                        {
                            return dereferenced;
                        }

                        if (dereferenced is JsonObject dereferencedObject)
                        {
                            foreach (var (key, value) in rest)
                            {
                                dereferencedObject[key] = value?.DeepClone();
                            }

                            return dereferencedObject;
                        }

                        return rest;
                    }
                }

                var clone = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    clone[key] = DereferenceJsonSchema(value, schemas, seenRefs);
                }

                return clone;
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonObject CloneWithoutRef(JsonObject obj, JsonObject schemas, HashSet<string> seenRefs)
    {
        var clone = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, "$ref", StringComparison.Ordinal))
            {
                continue;
            }

            clone[key] = DereferenceJsonSchema(value, schemas, seenRefs);
        }

        return clone;
    }

    private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (data is not null)
        {
            error["data"] = data.DeepClone();
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = error,
        };
    }

    private static JsonNode? NormalizeJsonRpcId(JsonObject message)
    {
        if (!message.TryGetPropertyValue("id", out var id) || id is null)
        {
            return null;
        }

        try
        {
            var element = id.Deserialize<JsonElement>(JsonDefaults.Transport);
            return element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null
                ? id.DeepClone()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonObject obj, string key)
    {
        try
        {
            return obj[key]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string SummarizeToolResponse(ToolResponse response)
    {
        if (string.Equals(response.Status, "ok", StringComparison.Ordinal))
        {
            return SanitizeInlineImageData(response.Result)?.ToJsonString(JsonDefaults.Storage) ?? "{\"status\":\"ok\"}";
        }

        var code = response.Error?.Code ?? response.Status.ToUpperInvariant();
        var message = response.Error?.Message ?? $"Tool returned status {response.Status}";
        return $"{response.Status}: {code}: {message}";
    }

    private static bool TryCreateImageContent(JsonNode? result, out JsonObject imageContent)
    {
        imageContent = new JsonObject();
        if (result is not JsonObject obj)
        {
            return false;
        }

        var mimeType = GetString(obj, "mimeType");
        var data = GetString(obj, "data") ?? GetString(obj, "base64");
        var dataUrl = GetString(obj, "dataUrl");
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            var match = Regex.Match(dataUrl, "^data:([^;]+);base64,(.+)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                mimeType = match.Groups[1].Value;
                data = match.Groups[2].Value;
            }
        }

        if (string.IsNullOrWhiteSpace(mimeType) || string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        imageContent = new JsonObject
        {
            ["type"] = "image",
            ["mimeType"] = mimeType,
            ["data"] = data,
        };
        return true;
    }

    private static JsonNode? SanitizeInlineImageData(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(SanitizeInlineImageData(item));
                }

                return clone;
            }
            case JsonObject obj:
            {
                var clone = new JsonObject();
                var removeInlineImageFields = IsInlineImageObject(obj);
                var removedImageData = false;
                foreach (var (key, value) in obj)
                {
                    if (removeInlineImageFields &&
                        (string.Equals(key, "dataUrl", StringComparison.Ordinal) ||
                        string.Equals(key, "data", StringComparison.Ordinal) ||
                        string.Equals(key, "base64", StringComparison.Ordinal)))
                    {
                        removedImageData = true;
                        continue;
                    }

                    clone[key] = SanitizeInlineImageData(value);
                }

                if (removedImageData)
                {
                    clone["inlineImageReturned"] = true;
                }

                return clone;
            }
            default:
                return node.DeepClone();
        }
    }

    private static bool IsInlineImageObject(JsonObject obj)
    {
        var mimeType = GetString(obj, "mimeType");
        var hasImageMimeType = mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        return !string.IsNullOrWhiteSpace(GetString(obj, "dataUrl")) ||
               (hasImageMimeType &&
                (!string.IsNullOrWhiteSpace(GetString(obj, "data")) ||
                 !string.IsNullOrWhiteSpace(GetString(obj, "base64"))));
    }

    private static bool ToolRequiresWorkspaceRoot(string tool)
    {
        return !string.Equals(tool, "list_trusted_workspaces", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_gpt_instructions", StringComparison.Ordinal) &&
               !string.Equals(tool, "list_skills", StringComparison.Ordinal) &&
               !string.Equals(tool, "get_skill", StringComparison.Ordinal) &&
               !string.Equals(tool, "write_stdin", StringComparison.Ordinal) &&
               !string.Equals(tool, "request_permissions", StringComparison.Ordinal);
    }

    private static string ToTitleCase(string value)
    {
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    }

    private sealed class McpMethodException(int code, string message, JsonNode? dataNode = null) : Exception(message)
    {
        public int Code { get; } = code;

        public JsonNode? DataNode { get; } = dataNode;
    }
}
