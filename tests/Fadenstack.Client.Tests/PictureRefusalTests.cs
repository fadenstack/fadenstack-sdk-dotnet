using System.Net;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The three picture codes of the gateway (core 0.7.1 rc23) as <see cref="FadenstackApiException.PictureRefusal"/>.</summary>
public class PictureRefusalTests
{
    [Theory]
    [InlineData("picture_type_not_allowed", PictureRefusal.Type)]
    [InlineData("picture_missing", PictureRefusal.Missing)]
    [InlineData("embeds_not_allowed", PictureRefusal.Embeds)]
    public void A_400_with_a_picture_code_says_why(string code, PictureRefusal expected)
    {
        var error = new FadenstackApiException(HttpStatusCode.BadRequest, "raw", code, "invalid_request_error", "messages.0.content.1");

        Assert.Equal(expected, error.PictureRefusal);
        Assert.Equal(expected, FadenstackApiException.PictureRefusalOf(new InvalidOperationException("wrapped", error)));
    }

    [Fact]
    public void Another_code_or_another_status_is_no_picture_refusal()
    {
        Assert.Null(new FadenstackApiException(HttpStatusCode.BadRequest, "raw", "something_else", null, null).PictureRefusal);
        Assert.Null(new FadenstackApiException(HttpStatusCode.Forbidden, "raw", ErrorCodes.PictureMissing, null, null).PictureRefusal);
        Assert.Null(FadenstackApiException.PictureRefusalOf(new InvalidOperationException("no")));
        Assert.Null(FadenstackApiException.PictureRefusalOf(null));
    }
}
