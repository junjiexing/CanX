// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using CanX.Plugins.Html;
using Xunit;

namespace CanX.Plugins.Tests;

public sealed class PluginHtmlExtractorTests
{
    [Fact]
    public void Extract_ReturnsRelativeTextAttributesAndArrays()
    {
        var request = JsonSerializer.Serialize(new
        {
            html = "<ul><li data-id='1'><a href='/a'><b>A</b></a><a href='/b'>B</a></li></ul>",
            selector = "li",
            fields = new Dictionary<string, object>
            {
                ["id"] = new { value = "attribute", attribute = "data-id" },
                ["title"] = new { selector = "a", value = "text" },
                ["links"] = new { selector = "a", value = "attribute", attribute = "href", all = true },
                ["markup"] = new { selector = "a", value = "html" },
            },
        });

        using var result = JsonDocument.Parse(PluginHtmlExtractor.Extract(request));
        var row = Assert.Single(result.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("1", row.GetProperty("id").GetString());
        Assert.Equal("A", row.GetProperty("title").GetString());
        Assert.Equal(["/a", "/b"], row.GetProperty("links").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("<b>A</b>", row.GetProperty("markup").GetString());
    }

    [Fact]
    public void Extract_RejectsInvalidSelectors()
    {
        var request = JsonSerializer.Serialize(new
        {
            html = "<p>test</p>",
            selector = "[[",
            fields = new Dictionary<string, object> { ["text"] = new { value = "text" } },
        });

        var exception = Assert.Throws<PluginHtmlException>(() => PluginHtmlExtractor.Extract(request));
        Assert.Equal("invalid_html_request", exception.Code);
    }
}
