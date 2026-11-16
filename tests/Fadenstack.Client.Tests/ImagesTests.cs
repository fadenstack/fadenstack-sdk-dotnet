using System.Net;
using Fadenstack.Auth;
using Fadenstack.Models;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>Pictures for an agent's tools: the gateway's images route under the agent.</summary>
public class ImagesTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    [Fact]
    public async Task A_prompt_goes_to_the_agents_images_route_and_the_pictures_come_back_decoded()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/powerpoint-assistant/images/generations",
            new { created = 1, data = new[] { new { b64_json = Png, revised_prompt = "a bar chart" } } });
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "powerpoint-assistant", Credentials = new StaticTokenProvider("t"),
            HttpClient = new HttpClient(server),
        });

        var images = await client.Agent!.GenerateImagesAsync(new ImageRequest
        {
            Prompt = "A bar chart of quarterly sales", Size = "1280x720", NumInferenceSteps = 8, NegativePrompt = "text",
        });

        Assert.Equal(Convert.FromBase64String(Png), Assert.Single(images.Pictures));
        Assert.Equal("a bar chart", images.Data[0].RevisedPrompt);
        var sent = server.Requests.Single().Json;
        Assert.Equal(("A bar chart of quarterly sales", "1280x720", 8, "text", 1),
            (sent.GetProperty("prompt").GetString(), sent.GetProperty("size").GetString(), sent.GetProperty("num_inference_steps").GetInt32(),
             sent.GetProperty("negative_prompt").GetString(), sent.GetProperty("n").GetInt32()));
        Assert.False(sent.TryGetProperty("model", out _), "the server's image model unless one is named");
        Assert.False(sent.TryGetProperty("seed", out _));
    }

    [Fact]
    public async Task A_server_without_an_image_model_says_so()
    {
        var server = new FakeServer().Json("POST", "/v1/agents/powerpoint-assistant/images/generations",
            new { error = new { code = "no_image_model", message = "No image model is set up here: an administrator hosts one first." } },
            HttpStatusCode.BadRequest);
        using var client = new FadenstackClient(new FadenstackClientOptions
        {
            ServerUrl = new Uri("https://faden.test"), Agent = "powerpoint-assistant", Credentials = new StaticTokenProvider("t"),
            HttpClient = new HttpClient(server),
        });

        var error = await Assert.ThrowsAnyAsync<FadenstackException>(() => client.Agent!.GenerateImagesAsync(new ImageRequest { Prompt = "x" }));
        Assert.Contains("No image model", error.Message);
    }

    private static FadenstackClient ClientFor(FakeServer server) => new(new FadenstackClientOptions
    {
        ServerUrl = new Uri("https://faden.test"), Agent = "powerpoint-assistant", Credentials = new StaticTokenProvider("t"),
        HttpClient = new HttpClient(server),
    });

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "images_disabled", "invalid_request_error", true)]
    [InlineData(HttpStatusCode.Forbidden, "agent_not_granted", "permission_error", false)]
    [InlineData(HttpStatusCode.Forbidden, null, "permission_error", false)]
    [InlineData(HttpStatusCode.BadRequest, "images_disabled", "invalid_request_error", false)]
    public async Task A_refusal_with_images_disabled_is_told_apart_from_other_errors(HttpStatusCode status, string? code, string type, bool expected)
    {
        var server = new FakeServer().Json("POST", "/v1/agents/powerpoint-assistant/images/generations",
            new { error = new { message = "No.", type, code } }, status);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAnyAsync<FadenstackApiException>(() => client.Agent!.GenerateImagesAsync(new ImageRequest { Prompt = "x" }));

        Assert.Equal(expected, error.IsImagesDisabled);
        Assert.Equal(expected, error.Code == ErrorCodes.ImagesDisabled && (int)error.Status == 403);
        Assert.Equal("images_disabled", ErrorCodes.ImagesDisabled);
    }
}
