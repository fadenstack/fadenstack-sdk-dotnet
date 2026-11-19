using Fadenstack.Logging;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>Copied details name no user: the profile folder becomes %USERPROFILE% (Windows) or ~ (elsewhere).</summary>
public class DiagnosticTextTests
{
    private const string Profile = @"C:\Users\anna";

    private static string Win(string text) => DiagnosticText.MaskUserPaths(text, Profile, windows: true);

    private static string Unix(string text, string profile = "/home/anna") => DiagnosticText.MaskUserPaths(text, profile, windows: false);

    [Fact]
    public void A_path_inside_an_exception_message_is_masked_with_either_slash()
    {
        Assert.Equal(@"Could not find file '%USERPROFILE%\Documents\plan.docx'.", Win(@"Could not find file 'C:\Users\anna\Documents\plan.docx'."));
        Assert.Equal("Could not find file '%USERPROFILE%/Documents/plan.docx'.", Win("Could not find file 'C:/Users/anna/Documents/plan.docx'."));
        Assert.Equal(@"System.IO.IOException: access to %USERPROFILE%\a.txt and %USERPROFILE%\b.txt",
            Win(@"System.IO.IOException: access to C:\Users\anna\a.txt and c:\users\ANNA\b.txt"));
    }

    [Fact]
    public void The_letter_case_does_not_matter_on_Windows_and_the_long_path_prefix_goes()
    {
        Assert.Equal(@"%USERPROFILE%\x", Win(@"c:\USERS\Anna\x"));
        Assert.Equal(@"%USERPROFILE%\x", Win(@"\\?\C:\Users\anna\x"));
        Assert.Equal(@"%USERPROFILE%\\x", Win(@"C:\\Users\\anna\\x")); // a path with escaped backslashes (JSON)
        Assert.Equal("file:///%USERPROFILE%/x", Win("file:///C:/Users/anna/x"));
    }

    [Fact]
    public void The_same_user_name_on_another_drive_is_masked_too()
    {
        Assert.Equal(@"%USERPROFILE%\x and %USERPROFILE%/y", Win(@"D:\Users\anna\x and C:/users/anna/y"));
        // The profile elsewhere than C:\Users\<name>: its own path and the C:\Users\<name> form both go.
        Assert.Equal(@"%USERPROFILE%\x and %USERPROFILE%\y",
            DiagnosticText.MaskUserPaths(@"E:\Profiles\anna\x and C:\Users\anna\y", @"E:\Profiles\anna", windows: true));
    }

    [Fact]
    public void A_path_of_another_user_is_left_alone()
    {
        const string other = @"Access to C:\Users\bert\Documents\x.txt and C:\Users\anna2\y and C:\Users\anna-old\z and C:\Users\anna.old\w is denied";
        Assert.Equal(other, Win(other));
        Assert.Equal(@"C:\Users\bert\x and %USERPROFILE%\y", Win(@"C:\Users\bert\x and C:\Users\anna\y"));
        Assert.Equal(@"D:\Data\Users\bert\x and C:\Program Files\x", Win(@"D:\Data\Users\bert\x and C:\Program Files\x"));
    }

    [Fact]
    public void The_profile_alone_at_the_end_of_a_sentence_or_a_quote_is_masked()
    {
        Assert.Equal(@"in %USERPROFILE%.", Win(@"in C:\Users\anna."));
        Assert.Equal(@"in ""%USERPROFILE%""", Win(@"in ""C:\Users\anna"""));
        Assert.Equal(@"%USERPROFILE%\", Win(@"C:\Users\anna\"));
    }

    [Fact]
    public void Elsewhere_the_profile_becomes_a_tilde_and_the_case_counts()
    {
        Assert.Equal("Could not find file '~/Documents/plan.docx'.", Unix("Could not find file '/home/anna/Documents/plan.docx'."));
        Assert.Equal("/home/bert/x and /home/Anna/y and /mnt/home/anna/z and ~/w", Unix("/home/bert/x and /home/Anna/y and /mnt/home/anna/z and /home/anna/w"));
        Assert.Equal("~/Library/x", Unix("/Users/anna/Library/x", "/Users/anna"));
        Assert.Equal("/home/annabel/x", Unix("/home/annabel/x"));
        Assert.Equal(@"C:\Users\anna\x", Unix(@"C:\Users\anna\x")); // no Windows form where the OS is not Windows
    }

    [Fact]
    public void Text_without_a_path_and_an_unknown_profile_stay_as_they_are()
    {
        Assert.Equal("nothing here", Win("nothing here"));
        Assert.Equal("", Win(""));
        Assert.Equal(@"C:\Users\anna\x", DiagnosticText.MaskUserPaths(@"C:\Users\anna\x", "", windows: true));
        Assert.Equal(@"C:\Users\anna\x", DiagnosticText.MaskUserPaths(@"C:\Users\anna\x", null, windows: true));
        Assert.Throws<ArgumentNullException>(() => DiagnosticText.MaskUserPaths(null!));
    }

    [Fact]
    public void The_real_profile_of_this_user_is_masked()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
        {
            return;
        }
        var masked = DiagnosticText.MaskUserPaths($"Could not find file '{Path.Combine(profile, "Documents", "plan.docx")}'.");
        var windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        var token = windows ? "%USERPROFILE%" : "~";
        Assert.StartsWith($"Could not find file '{token}", masked);
        Assert.DoesNotContain(profile, masked, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
