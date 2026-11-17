using System.Globalization;
using System.Net;
using Fadenstack.Agents;
using Fadenstack.Agents.Sessions;
using Fadenstack.Agents.Tests;
using Fadenstack.Client.Tests;
using Fadenstack.UI.Services;
using Fadenstack.UI.ViewModels;
using Xunit;

namespace Fadenstack.UI.Tests;

/// <summary>
/// A refused picture reaches the person in plain words, in five languages, in both modes (core 0.7.1 rc23): the check of a picture's first bytes
/// when it is attached, and the gateway's three picture codes. The TypeScript SDK's pictureRefused.test.ts.
/// </summary>
public class PictureRefusedTests
{
    private static readonly string[] Languages = { "en", "de", "fr", "es", "zh-Hans" };

    private static string Url(string type, params byte[] bytes) => $"data:{type};base64,{Convert.ToBase64String(bytes)}";

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    private static readonly string Png = Url("image/png", 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0);
    private static readonly string Jpeg = Url("image/jpeg", 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46);
    private static readonly string Gif87 = Url("image/gif", Ascii("GIF87a").Concat(new byte[] { 1, 0 }).ToArray());
    private static readonly string Gif89 = Url("image/gif", Ascii("GIF89a").Concat(new byte[] { 1, 0 }).ToArray());
    private static readonly string Webp = Url("image/webp", 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50);
    private static readonly string Bmp = Url("image/bmp", 0x42, 0x4D, 0x36, 0, 0, 0, 0, 0);
    private static readonly string Text = Url("image/png", Ascii("this is not a picture"));

    private static async Task<AgentChatViewModel> ChatAsync(Gateway gateway, string language = "en")
    {
        var broker = new ApprovalBroker();
        var agent = gateway.Builder().UseApprovals(broker).Build();
        var chat = new AgentChatViewModel(agent, broker, InlineDispatcher.Instance, new AgentChatOptions
        {
            RenderInterval = TimeSpan.Zero, Preferences = new MemoryPreferenceStore(), AutomaticLanguage = CultureInfo.GetCultureInfo(language),
        });
        await chat.InitializeAsync();
        return chat;
    }

    private static Gateway Seeing() => new(vision: "true");

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("gif87")]
    [InlineData("gif89")]
    [InlineData("webp")]
    public async Task A_PNG_JPEG_GIF_or_WebP_is_attached(string which)
    {
        var chat = await ChatAsync(Seeing().Says("Fine."));
        var url = which switch { "png" => Png, "jpeg" => Jpeg, "gif87" => Gif87, "gif89" => Gif89, _ => Webp };

        Assert.True(chat.AttachPicture(url));

        Assert.Single(chat.Attachments);
        Assert.Null(chat.Error);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public async Task A_BMP_or_text_data_is_refused_with_the_sentence_and_nothing_is_attached(string language)
    {
        var chat = await ChatAsync(Seeing(), language);
        var expected = chat.Strings.PictureTypeNotAllowed;

        foreach (var url in new[] { Bmp, Text })
        {
            Assert.False(chat.AttachPicture(url));
            Assert.Equal(expected, chat.Error);
        }
        Assert.Empty(chat.Attachments);
        Assert.NotEqual("PictureTypeNotAllowed", expected); // the text exists in this language
    }

    [Fact]
    public async Task An_empty_picture_is_refused_with_its_own_sentence()
    {
        var chat = await ChatAsync(Seeing());

        Assert.False(chat.AttachPicture("data:image/png;base64,"));

        Assert.Equal("A picture in your message was empty, so nothing was sent.", chat.Error);
        Assert.Empty(chat.Attachments);
    }

    [Fact]
    public async Task The_media_type_sent_is_the_one_the_bytes_show_not_the_urls_claim()
    {
        var gateway = Seeing().Says("A cat.");
        var chat = await ChatAsync(gateway);

        Assert.True(chat.AttachPicture(Jpeg.Replace("image/jpeg", "image/png")));
        await chat.SendAsync("What is it?");

        var user = gateway.ChatRequests[0].Json.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
        var sent = user.GetProperty("content").EnumerateArray().First().GetProperty("image_url").GetProperty("url").GetString();
        Assert.Equal(Jpeg, sent);
    }

    [Fact]
    public async Task A_picture_a_host_added_itself_is_checked_again_when_the_message_goes_and_nothing_of_it_is_sent()
    {
        var gateway = Seeing().Says("Fine.");
        var chat = await ChatAsync(gateway);
        chat.Attachments.Add(new AttachmentViewModel("Clipboard", "Picture",
            _ => Task.FromResult<MessageAttachment?>(new MessageAttachment { Title = "Clipboard", Content = Bmp, MediaType = "image/png", Kind = "picture" })));

        await chat.SendAsync("Look at this");

        var user = gateway.ChatRequests[0].Json.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
        Assert.DoesNotContain("image_url", user.ToString());
        Assert.Contains(chat.Turns[0].Parts, p => p is Fadenstack.UI.Parts.NoticePart);
    }

    private static Func<Recorded, HttpResponseMessage> Refuse(string code) => _ => FakeServer.Respond(HttpStatusCode.BadRequest,
        new { error = new { message = "raw server text", type = "invalid_request_error", param = "messages.0.content.1", code } });

    [Theory]
    [InlineData("picture_type_not_allowed")]
    [InlineData("picture_missing")]
    [InlineData("embeds_not_allowed")]
    public async Task Each_picture_code_of_the_gateway_has_its_sentence_in_five_languages(string code)
    {
        foreach (var language in Languages)
        {
            var chat = await ChatAsync(new Gateway().Then(Refuse(code)), language);

            await chat.SendAsync("Hi");

            var turn = Assert.Single(chat.Turns);
            Assert.Equal(TurnState.Failed, turn.State);
            var strings = chat.Strings;
            var expected = code switch
            {
                "picture_type_not_allowed" => strings.PictureTypeNotAllowed,
                "picture_missing" => strings.PictureMissing,
                _ => strings.EmbedsNotAllowed,
            };
            Assert.Equal(expected, turn.ErrorMessage);
            Assert.DoesNotContain("raw server text", turn.ErrorMessage);
        }
    }

    [Fact]
    public async Task Another_400_keeps_the_servers_own_text()
    {
        var chat = await ChatAsync(new Gateway().Then(Refuse("something_else")));

        await chat.SendAsync("Hi");

        Assert.Equal("raw server text", chat.Turns[0].ErrorMessage);
    }

    [Fact]
    public void The_English_sentences_are_the_ones_the_owner_suggested()
    {
        var english = UiStrings.For(CultureInfo.GetCultureInfo("en"));
        Assert.Equal("This picture's format can't be sent. Use a PNG, JPEG, WebP or GIF picture.", english.PictureTypeNotAllowed);
        Assert.Equal("A picture in your message was empty, so nothing was sent.", english.PictureMissing);
        Assert.Equal("The server does not accept pictures in this form.", english.EmbedsNotAllowed);
    }

    [Fact]
    public void Every_language_has_the_three_sentences()
    {
        foreach (var language in Languages)
        {
            var s = UiStrings.For(CultureInfo.GetCultureInfo(language));
            Assert.All(new[] { s.PictureTypeNotAllowed, s.PictureMissing, s.EmbedsNotAllowed },
                text => Assert.False(string.IsNullOrWhiteSpace(text) || text is "PictureTypeNotAllowed" or "PictureMissing" or "EmbedsNotAllowed"));
        }
    }
}
