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
    private Task<object> StatPathAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(absolutePath))
        {
            return Task.FromResult<object>(new
            {
                path = targetPath,
                stat = new PathStatResult
                {
                    Path = targetPath,
                    Kind = "directory",
                    Size = 0,
                    ModifiedAt = Directory.GetLastWriteTimeUtc(absolutePath).ToString("O"),
                },
            });
        }

        var fileInfo = new FileInfo(absolutePath);
        return Task.FromResult<object>(new
        {
            path = targetPath,
            stat = new PathStatResult
            {
                Path = targetPath,
                Kind = "file",
                Size = fileInfo.Length,
                ModifiedAt = fileInfo.LastWriteTimeUtc.ToString("O"),
            },
        });
    }

    private async Task<object> MakeDirAsync(string workspaceRoot, string targetPath, CancellationToken cancellationToken)
    {
        var absolutePath = _pathPolicy.ResolvePathWithinWorkspace(workspaceRoot, targetPath);
        await Task.Run(() => Directory.CreateDirectory(absolutePath), cancellationToken);
        return new
        {
            path = targetPath,
            created = true,
        };
    }
}
