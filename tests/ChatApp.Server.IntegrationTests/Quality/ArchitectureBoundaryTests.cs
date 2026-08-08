using System.Text.RegularExpressions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

/// <summary>
/// 保持 Host/API 与持久化实现解耦：Controller 只能依赖 Core 契约，
/// 不能把 DbContext 或 Infrastructure 类型带回请求编排层。
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly Regex InfrastructureUsing = new(
        @"^\s*using\s+Infrastructure\.",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void Controllers_DoNotReferenceInfrastructureNamespacesOrDbContext()
    {
        var root = FindRepoRoot();
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "Controllers"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (InfrastructureUsing.IsMatch(source))
                failures.Add($"{Path.GetRelativePath(root, file)} imports Infrastructure");
            if (Regex.IsMatch(source, @"\bUserDbContext\b"))
                failures.Add($"{Path.GetRelativePath(root, file)} references UserDbContext");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChatApp.Server.csproj"))
                && Directory.Exists(Path.Combine(dir.FullName, "Controllers")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Cannot locate ChatApp.Server repo root.");
    }
}
