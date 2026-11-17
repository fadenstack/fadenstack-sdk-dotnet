using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>The check of a picture by its first bytes, as the gateway judges it (core 0.7.1 rc23). TypeScript: <c>checkPicture</c>.</summary>
public class PictureDataTests
{
    private static string Url(string type, params byte[] bytes) => $"data:{type};base64,{Convert.ToBase64String(bytes)}";

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 1, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    public void The_first_bytes_name_PNG_JPEG_GIF_and_WebP(byte[] bytes, string type)
    {
        Assert.Equal(type, PictureData.TypeOf(bytes));
        Assert.Equal(type, PictureData.Check(Url("image/x-claimed", bytes)).MediaType); // the bytes decide, not the claim
    }

    [Fact]
    public void Anything_else_is_not_a_picture_and_empty_data_is_missing()
    {
        Assert.Null(PictureData.TypeOf(new byte[] { 0x42, 0x4D, 0x36, 0, 0, 0 })); // BMP
        Assert.Null(PictureData.TypeOf(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 })); // RIFF, but WAVE
        Assert.Equal(PictureProblem.Type, PictureData.Check(Url("image/png", System.Text.Encoding.ASCII.GetBytes("plain text"))).Problem);
        Assert.Equal(PictureProblem.Type, PictureData.Check("https://example.com/cat.png").Problem);
        foreach (var empty in new[] { "", "  ", "data:image/png;base64,", "data:image/png," })
        {
            Assert.Equal(PictureProblem.Missing, PictureData.Check(empty).Problem);
        }
        Assert.Equal(PictureProblem.Missing, PictureData.Check(null).Problem);
    }

    [Fact]
    public void A_picture_that_passes_comes_back_with_the_type_its_bytes_show_in_its_url()
    {
        var jpeg = Url("image/jpeg", 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46);

        var check = PictureData.Check(jpeg.Replace("image/jpeg", "image/png"));

        Assert.True(check.Ok);
        Assert.Equal(("image/jpeg", jpeg), (check.MediaType, check.DataUrl));
        var percent = PictureData.Check("data:image/jpeg,%89PNG%0D%0A%1A%0A%00%00");
        Assert.Equal("image/png", percent.MediaType);
        Assert.StartsWith("data:image/png;base64,", percent.DataUrl);
    }
}
