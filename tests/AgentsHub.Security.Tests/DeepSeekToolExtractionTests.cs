using System.Text.Json;
using AgentsHub.Services.DeepSeek;
using Xunit;

namespace AgentsHub.Security.Tests;

/// <summary>
/// Tool-request extraction from fenced replies, including the shapes small models actually produce:
/// several objects packed into one fence, and prose wrapped around the blocks.
/// </summary>
public class DeepSeekToolExtractionTests
{
    private static string Action(JsonElement call) => call.GetProperty("action").GetString()!;

    [Fact]
    public void Extract_OneObjectPerFence_ReturnsEachCall()
    {
        const string reply =
            """
            ```tool
            {"action": "list_dir", "path": "."}
            ```
            ```tool
            {"action": "read_file", "path": "app.py"}
            ```
            """;

        var calls = DeepSeekTools.Extract(reply);
        Assert.Equal(2, calls.Count);
        Assert.Equal("list_dir", Action(calls[0]));
        Assert.Equal("read_file", Action(calls[1]));
    }

    [Fact]
    public void Extract_MultipleObjectsInOneFence_ReturnsEachCall()
    {
        const string reply =
            """
            ```tool
            {"action": "list_dir", "path": "."}
            {"action": "read_file", "path": "src/main.cpp"}
            {"action": "search_text", "query": "TODO", "path": "."}
            ```
            """;

        var calls = DeepSeekTools.Extract(reply);
        Assert.Equal(3, calls.Count);
        Assert.Equal("list_dir", Action(calls[0]));
        Assert.Equal("read_file", Action(calls[1]));
        Assert.Equal("search_text", Action(calls[2]));
    }

    [Fact]
    public void Extract_ProseAroundBlocks_FindsTheCalls()
    {
        const string reply = "Let me look.\n\n```tool\n{\"action\": \"list_dir\", \"path\": \"src\"}\n```\n\nDone asking.";

        var calls = DeepSeekTools.Extract(reply);
        var call = Assert.Single(calls);
        Assert.Equal("list_dir", Action(call));
    }

    [Fact]
    public void Extract_UnknownActionInsidePackedBlock_IsNotReturned()
    {
        const string reply =
            """
            ```tool
            {"action": "edit_file", "path": "a.py"}
            {"action": "list_dir", "path": "."}
            ```
            """;

        var calls = DeepSeekTools.Extract(reply);
        var call = Assert.Single(calls);
        Assert.Equal("list_dir", Action(call));
        Assert.Equal(["edit_file"], DeepSeekTools.UnknownActions(reply));
    }

    [Fact]
    public void Strip_PackedToolBlock_IsRemovedFromVisibleText()
    {
        const string reply = "Reading now.\n```tool\n{\"action\": \"list_dir\", \"path\": \".\"}\n{\"action\": \"read_file\", \"path\": \"a.py\"}\n```\nThat is all.";

        string visible = DeepSeekTools.Strip(reply);
        Assert.DoesNotContain("action", visible);
        Assert.DoesNotContain("```", visible);
        Assert.Contains("Reading now.", visible);
        Assert.Contains("That is all.", visible);
    }

    [Fact]
    public void Extract_BareObjectStillWorks()
    {
        const string reply = "{\"action\": \"list_dir\", \"path\": \".\"}";

        var calls = DeepSeekTools.Extract(reply);
        var call = Assert.Single(calls);
        Assert.Equal("list_dir", Action(call));
    }
}
