using PortableCodex.Native.Services;

namespace PortableCodex.Core.Tests;

public sealed class PathPolicyServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-codex-tests", Guid.NewGuid().ToString("N"));

    public PathPolicyServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "hello.txt"), "hello");
    }

    [Fact]
    public void ResolvePathWithinWorkspaceAllowsDescendants()
    {
        var service = new PathPolicyService();

        var resolved = service.ResolvePathWithinWorkspace(_root, "src/hello.txt");

        Assert.Equal(Path.Combine(_root, "src", "hello.txt"), resolved);
    }

    [Fact]
    public void ResolvePathWithinWorkspaceRejectsEscapes()
    {
        var service = new PathPolicyService();

        Assert.Throws<InvalidOperationException>(() => service.ResolvePathWithinWorkspace(_root, "../outside.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
