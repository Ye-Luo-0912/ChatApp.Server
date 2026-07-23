using Infrastructure.Services;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Auth;

public sealed class ChatExportAttachmentParserTests
{
    [Fact]
    public void Extract_JsonAttachments_AndDedupesUrlScan()
    {
        var content =
            """{"attachments":[{"url":"https://cdn.example/a.png","name":"a.png","mime":"image/png"}],"text":"see https://cdn.example/a.png"}""";
        var items = ChatExportAttachmentParser.Extract("m1", 100, content);
        Assert.Single(items);
        Assert.Equal("https://cdn.example/a.png", items[0].Url);
        Assert.Equal("a.png", items[0].Name);
        Assert.Equal("image/png", items[0].ContentType);
        Assert.Equal("json", items[0].Source);
    }

    [Fact]
    public void Extract_PlainUrl_GuessesContentType()
    {
        var items = ChatExportAttachmentParser.Extract(
            "m2", 200, "file: https://files.example/doc.pdf?x=1");
        Assert.Single(items);
        Assert.StartsWith("https://files.example/doc.pdf", items[0].Url, StringComparison.Ordinal);
        Assert.Equal("application/pdf", items[0].ContentType);
        Assert.Equal("url_scan", items[0].Source);
    }

    [Fact]
    public void Extract_Empty_ReturnsEmpty()
    {
        Assert.Empty(ChatExportAttachmentParser.Extract("m3", 1, "hello"));
    }
}
