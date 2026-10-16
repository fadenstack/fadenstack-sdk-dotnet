using System.Text.Json;
using Fadenstack.Agents.Sessions;
using Fadenstack.Http;
using Microsoft.Extensions.AI;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>Pictures the user attached: image parts before the text, the newest only, named in place of the older ones.</summary>
public class HistoryPictureTests
{
    private static string Picture(int n) => "data:image/jpeg;base64," + Convert.ToBase64String(BitConverter.GetBytes(n));

    private static TranscriptEntry Entry(string type, string turnId, object data, string? status = null) => new()
    {
        Type = type, TurnId = turnId, Status = status, Data = JsonSerializer.SerializeToElement(data, FadenstackJson.Options),
    };

    private static object PictureAttachment(int n, string title, string kind = "picture") =>
        new { title, content = Picture(n), kind, media_type = "image/jpeg" };

    /// <summary>A finished turn: the user's message (with these pictures) and an answer.</summary>
    private static IEnumerable<TranscriptEntry> Turn(string turnId, string text, params int[] pictures)
    {
        object user = pictures.Length == 0 ? new { text } : new { text, attachments = pictures.Select(n => PictureAttachment(n, $"Picture {n}")).ToList() };
        yield return Entry(EntryTypes.UserMessage, turnId, user);
        yield return Entry(EntryTypes.AssistantMessage, turnId, new { text = $"About {text}", tool_calls = Array.Empty<object>(), origin = "model" });
        yield return Entry(EntryTypes.TurnEnd, turnId, new { }, TurnStatus.Ok);
    }

    /// <summary>A message's parts as the model gets them: "image:url" or "text:...".</summary>
    private static List<string> Parts(ChatMessage message) => message.Contents.Select(c => c switch
    {
        DataContent d => "image:" + d.Uri,
        TextContent t => "text:" + t.Text,
        _ => c.GetType().Name,
    }).ToList();

    private static List<string> SentPictures(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents.OfType<DataContent>()).Select(d => d.Uri).ToList();

    [Fact]
    public void Go_as_image_parts_before_the_text_the_other_attachments_in_the_text()
    {
        var entries = new[]
        {
            Entry(EntryTypes.UserMessage, "t1", new
            {
                text = "Which is cheaper?",
                attachments = new object[]
                {
                    PictureAttachment(1, "Offer A", "screenshot"),
                    new { title = "Selection", content = "Offer B: 40 EUR", kind = "selection", media_type = "text/markdown" },
                },
            }),
        };
        var user = Assert.Single(new ContextBuilder().Build(entries));
        Assert.Equal(ChatRole.User, user.Role);
        Assert.Equal(new[]
        {
            "image:" + Picture(1),
            "text:<attachment title=\"Selection\" kind=\"selection\">\nOffer B: 40 EUR\n</attachment>\n\nWhich is cheaper?",
        }, Parts(user));
    }

    [Fact]
    public void Go_with_the_newest_messages_only_and_older_ones_are_named_in_their_place()
    {
        var entries = Turn("t1", "first", 1, 2).Concat(Turn("t2", "second", 3, 4, 5)).Concat(Turn("t3", "third", 6)).ToList();
        var messages = new ContextBuilder().Build(entries);
        Assert.Equal(new[] { Picture(3), Picture(4), Picture(5), Picture(6) }, SentPictures(messages));
        Assert.True(SentPictures(messages).Count <= ContextBuilder.MaxHistoryPictures);
        Assert.Equal(new[]
        {
            "text:<attachment title=\"Picture 1\" kind=\"picture\">\n[A picture the user attached earlier; it is not sent again.]\n</attachment>\n\n" +
            "<attachment title=\"Picture 2\" kind=\"picture\">\n[A picture the user attached earlier; it is not sent again.]\n</attachment>\n\nfirst",
        }, Parts(messages[0]));
    }

    [Fact]
    public void The_newest_message_keeps_its_pictures_even_when_it_has_more_than_the_history_may_carry()
    {
        var pictures = Enumerable.Range(1, ContextBuilder.MaxHistoryPictures + 2).ToArray();
        var messages = new ContextBuilder().Build(Turn("t1", "old", 99).Concat(Turn("t2", "many", pictures)).ToList());
        Assert.Equal(pictures.Length, messages[messages.Count - 2].Contents.OfType<DataContent>().Count());
        Assert.Empty(messages[0].Contents.OfType<DataContent>());
    }

    [Fact]
    public void Merge_with_a_message_before_them_that_has_none_a_turn_whose_answer_was_refused()
    {
        var entries = new[]
        {
            Entry(EntryTypes.UserMessage, "t1", new { text = "first" }),
            Entry(EntryTypes.TurnEnd, "t1", new { }, TurnStatus.ResponseBlocked),
            Entry(EntryTypes.UserMessage, "t2", new { text = "look", attachments = new[] { new { title = "P", content = Picture(1), media_type = "image/png" } } }),
        };
        var user = Assert.Single(new ContextBuilder().Build(entries));
        Assert.Equal(new[] { "text:first", "image:" + Picture(1), "text:look" }, Parts(user));
    }

    [Fact]
    public void Are_left_out_of_a_message_whose_turn_failed_with_the_rest_of_it()
    {
        var entries = new[]
        {
            Entry(EntryTypes.UserMessage, "t1", new { text = "broken", attachments = new[] { new { title = "P", content = Picture(1), media_type = "image/png" } } }),
            Entry(EntryTypes.TurnEnd, "t1", new { }, TurnStatus.Error),
        }.Concat(Turn("t2", "next")).ToList();
        Assert.All(new ContextBuilder().Build(entries), m => Assert.All(m.Contents, c => Assert.IsType<TextContent>(c)));
    }

    [Fact]
    public void A_picture_whose_url_is_broken_is_named_never_sent_as_text()
    {
        var entries = new[]
        {
            Entry(EntryTypes.UserMessage, "t1", new { text = "look", attachments = new[] { new { title = "P", content = "data:image/png;base64,***", media_type = "image/png" } } }),
        };
        var user = Assert.Single(new ContextBuilder().Build(entries));
        Assert.Equal(new[] { "text:<attachment title=\"P\">\n[A picture the user attached earlier; it is not sent again.]\n</attachment>\n\nlook" }, Parts(user));
    }

    [Fact]
    public async Task A_picture_reaches_the_model_as_an_image_part_on_the_wire()
    {
        var gateway = new Gateway(vision: "true").Says("The error says the disk is full.");
        using var agent = gateway.Builder().Build();
        var session = await agent.StartSessionAsync();

        await session.SendAsync("What does this error say?", new SendOptions
        {
            Attachments = [new MessageAttachment { Title = "Screenshot", Content = Picture(7), Kind = "screenshot", MediaType = "image/jpeg" }],
        });

        var content = gateway.ChatRequests[0].Json.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(new[] { "image_url", "text" }, content.Select(p => p.GetProperty("type").GetString()));
        Assert.Equal(Picture(7), content[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("What does this error say?", content[1].GetProperty("text").GetString());
        // The transcript keeps the picture with the message, so the chat shows it when opened again.
        var stored = session.Transcript.First(e => e.Type == EntryTypes.UserMessage).Data.GetProperty("attachments")[0];
        Assert.Equal((Picture(7), "image/jpeg"), (stored.GetProperty("content").GetString(), stored.GetProperty("media_type").GetString()));
    }
}
