using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

/// <summary>
/// 防止 Controllers 中文响应再次被错误编码破坏为问号串。
/// </summary>
public sealed class Utf8SourceEncodingTests
{
    private static readonly Regex CorruptedPlaceholders = new(
        @"Message\s*=\s*""\?{3,}""",
        RegexOptions.Compiled);

    private static readonly Regex CorruptedXmlDoc = new(
        @"///\s*<summary>\s*\r?\n\s*///\s*\?{3,}",
        RegexOptions.Compiled);

    [Fact]
    public void Controllers_SourceFiles_HaveNoCorruptedQuestionMarkMessages()
    {
        var root = FindRepoRoot();
        var controllers = Path.Combine(root, "Controllers");
        Assert.True(Directory.Exists(controllers), $"missing Controllers at {controllers}");

        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(controllers, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (text.Contains('\uFFFD'))
                failures.Add($"{Path.GetRelativePath(root, file)}: contains U+FFFD replacement char");

            foreach (Match m in CorruptedPlaceholders.Matches(text))
                failures.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, m.Index)}: {m.Value}");

            foreach (Match m in CorruptedXmlDoc.Matches(text))
                failures.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, m.Index)}: corrupted xml doc");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Controllers_ContainReadableChineseUserMessages()
    {
        var root = FindRepoRoot();
        var usersController = Path.Combine(root, "Controllers", "UsersController.cs");
        var text = File.ReadAllText(usersController, Encoding.UTF8);
        Assert.Contains("用户不存在", text, StringComparison.Ordinal);
        Assert.Contains("更新成功", text, StringComparison.Ordinal);
        Assert.Contains("已预约注销", text, StringComparison.Ordinal);
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

        throw new InvalidOperationException("Cannot locate ChatApp.Server repo root from test base directory.");
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }

        return line;
    }
}
