using System.Diagnostics;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>The repository itself: no rule in .gitignore hides a source file.</summary>
public class RepositoryTests
{
    /// <summary>
    /// Folders the SDK has or is likely to get, and a new file in one. A case-insensitive checkout (Windows, macOS)
    /// matched the Snapcraft rule <c>parts/</c> against <c>src/Fadenstack.UI/Parts/</c>, so a new part there never
    /// showed in <c>git status</c> (sdk/51 I00 #1, I-29); <c>stage/</c> and <c>prime/</c> did the same. A probe for a
    /// folder that does not exist ends in a slash, or git takes it for a file.
    /// </summary>
    private static readonly string[] Probes =
    {
        "src/Fadenstack.UI/Parts/",
        "src/Fadenstack.UI/Parts/NewPart.cs",
        "src/Fadenstack.Agents/Stage/",
        "src/Fadenstack.Client/Prime/",
    };

    [Fact]
    public void No_tracked_file_and_no_new_source_file_is_ignored_on_a_case_insensitive_checkout()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            return; // a source archive without git: nothing to check
        }
        // core.ignorecase=true forces what a Windows or macOS checkout does, so the test fails on Linux CI as well.
        var tracked = Git(root, "-c core.ignorecase=true ls-files", input: null).Output
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var paths = tracked.Concat(Probes).ToList();

        var (code, ignored, error) = Git(root, "-c core.ignorecase=true check-ignore --no-index -v --stdin",
            string.Join("\n", paths) + "\n");

        // check-ignore exits 1 when nothing matches, 0 when something does. Each line is "<file>:<line>:<pattern>\t<path>".
        // A blank pattern is no rule: with --no-index, a checkout's CRLF .gitignore makes a blank line the "pattern" "\r",
        // which git status itself does not apply.
        Assert.True(code is 0 or 1, error);
        var rules = ignored.Split('\n').Where(line => line.Contains('\t'))
            .Where(line => line.Substring(0, line.IndexOf('\t')).Split(new[] { ':' }, 3).ElementAtOrDefault(2)?.Trim().Length > 0)
            .ToList();
        Assert.True(rules.Count == 0, "Ignored by a .gitignore rule:\n" + string.Join("\n", rules));
    }

    /// <summary>
    /// No source holds an invisible character as itself: a zero-width space, a BOM inside a file, a variation selector, a tag
    /// character or any other format character is unreviewable there -- and U+FEFF and the variation selectors are what the
    /// SDK's own invisible text mark is made of, so a stray one could pass for a mark. Write them as escapes (<c>\uFEFF</c>).
    /// </summary>
    [Fact]
    public void No_source_file_holds_an_invisible_character_as_itself()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            return; // a source archive without git: nothing to check
        }
        var found = new List<string>();
        foreach (var file in Git(root, "ls-files", input: null).Output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".axaml" or ".resx" or ".json" or ".md" or ".csproj" or ".props" or ".targets"))
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1); // a BOM at the very start is the file's encoding, not content
            }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                for (var j = 0; j < lines[i].Length; j++)
                {
                    var c = lines[i][j];
                    var code = char.IsSurrogatePair(lines[i], j) ? char.ConvertToUtf32(lines[i], j) : c;
                    if (code is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF or (>= 0xFE00 and <= 0xFE0F)
                        or (>= 0xE0000 and <= 0xE01EF) || (code < 0x10000 && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                    {
                        found.Add($"{file}:{i + 1}:{j + 1}: U+{code:X4}");
                    }
                }
            }
        }
        Assert.True(found.Count == 0, "Invisible characters written as themselves:\n" + string.Join("\n", found));
    }

    private static string? RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fadenstack.slnx")))
        {
            root = root.Parent;
        }
        // A worktree has a .git file, a clone a .git folder.
        return root is not null && (Directory.Exists(Path.Combine(root.FullName, ".git")) || File.Exists(Path.Combine(root.FullName, ".git")))
            ? root.FullName
            : null;
    }

    private static (int Code, string Output, string Error) Git(string root, string arguments, string? input)
    {
        var start = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var git = Process.Start(start)!;
        // Read both pipes before writing: a full output pipe would otherwise block git while it reads its input.
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            git.StandardInput.Write(input);
            git.StandardInput.Close();
        }
        git.WaitForExit();
        return (git.ExitCode, output.Result, error.Result);
    }
}
