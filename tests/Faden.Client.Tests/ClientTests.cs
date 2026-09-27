using LLMPort.Client;
using LLMPort.Client.Resources;
using Xunit;

namespace LLMPort.Client.Tests;

public class ClientTests
{
    [Fact]
    public void Client_Has_All_Resources()
    {
        var client = new LLMPortClient(new LLMPortClientOptions
        {
            BaseUrl = "https://llmport.test",
            ApiKey = "test-token",
        });

        Assert.NotNull(client.Sessions);
        Assert.NotNull(client.Projects);
        Assert.NotNull(client.Tools);
        Assert.NotNull(client.Memory);
        Assert.NotNull(client.Attachments);
        Assert.NotNull(client.Services);

        Assert.IsType<SessionsResource>(client.Sessions);
        Assert.IsType<ProjectsResource>(client.Projects);
        Assert.IsType<ToolsResource>(client.Tools);
        Assert.IsType<MemoryResource>(client.Memory);
        Assert.IsType<AttachmentsResource>(client.Attachments);
        Assert.IsType<ServicesResource>(client.Services);

        client.Dispose();
    }

    [Fact]
    public void Client_Creates_ChatClient()
    {
        var client = new LLMPortClient(new LLMPortClientOptions
        {
            BaseUrl = "https://llmport.test",
            ApiKey = "test-token",
        });

        var chatClient = client.GetChatClient("gpt-4o");
        Assert.NotNull(chatClient);

        client.Dispose();
    }
}
