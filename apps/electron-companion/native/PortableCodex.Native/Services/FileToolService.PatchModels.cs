using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using PortableCodex.Native.Models;
using PortableCodex.Native.Utils;

namespace PortableCodex.Native.Services;

public sealed partial class FileToolService
{
    private sealed record CodexPatchLine(char Prefix, string Text);

    private sealed record CodexPatchSection(string Action, string Path, string? MoveTo, List<string> Lines);

    private sealed class CodexPatchChange
    {
        public string Path { get; set; } = string.Empty;

        public string AbsolutePath { get; set; } = string.Empty;

        public bool OriginalExists { get; set; }

        public string? OriginalContent { get; set; }

        public string? NewContent { get; set; }

        public string Action { get; set; } = "update";
    }
}
