using System.IO.Compression;
using System.Text;
using Fadenstack.SkillAuthoring;
using Xunit;

namespace Fadenstack.Skills.Tests;

/// <summary>Import of a folder or a zip (a hostile one above all) and export: nothing is written before every check passes.</summary>
public class SkillImportExportTests
{
    private const string GoodSkillMd = "---\nname: contract-review\ndescription: Reviews a contract clause by clause.\n---\n## Goal\nReview it.\n";

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries) => Zip(null, entries);

    private static byte[] Zip(Action<ZipArchiveEntry>? each, params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                each?.Invoke(entry);
                using var content = entry.Open();
                content.Write(bytes, 0, bytes.Length);
            }
        }
        return stream.ToArray();
    }

    private static (string, byte[]) Text(string name, string text) => (name, new UTF8Encoding(false).GetBytes(text));

    private static void AssertRefusedAndNothingWritten(WorkshopRig rig, ImportAnalysis analysis, params SkillIssueCode[] expected)
    {
        Assert.False(analysis.Ok);
        Assert.True(expected.Length == 0 || analysis.Issues.Any(i => i.IsError && expected.Contains(i.Code)), string.Join(", ", analysis.Issues.Select(i => i.Code)));
        Assert.False(Directory.Exists(rig.Folder), "an analysis wrote something");
        Assert.False(Directory.Exists(rig.Staging));
    }

    [Fact]
    public async Task A_good_zip_with_a_folder_is_read_and_checked_and_nothing_is_written_until_it_is_committed()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("contract-review/SKILL.md", GoodSkillMd), Text("contract-review/faden.yaml", "trigger_rules:\n  keywords:\n    - clause\n"),
            Text("contract-review/references/tone.md", "Warm."));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "contract-review.zip");

        Assert.True(analysis.Ok);
        Assert.False(analysis.WouldReplace);
        Assert.Equal("contract-review", analysis.Document!.Name);
        Assert.Equal(new[] { "clause" }, analysis.Document.Extension.Keywords);
        Assert.Equal("references/tone.md", analysis.Document.Resources.Single().Path);
        Assert.Equal(3, analysis.FileCount);
        Assert.False(Directory.Exists(rig.Folder));

        var committed = await rig.Workshop.CommitImportAsync(analysis, false);

        Assert.Equal(SkillSaveStatus.Saved, committed.Status);
        Assert.True(File.Exists(Path.Combine(rig.Folder, "contract-review", "references", "tone.md")));
        Assert.False(Directory.Exists(rig.Staging));
    }

    [Fact]
    public async Task A_zip_without_a_folder_is_read_too_and_a_folder_of_another_name_is_said_not_refused()
    {
        using var rig = new WorkshopRig();

        var flat = await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd)), "flat.zip");
        var renamed = await rig.Workshop.AnalyzeZipAsync(Zip(Text("contract-review-main/SKILL.md", GoodSkillMd)), "main.zip");

        Assert.True(flat.Ok);
        Assert.True(renamed.Ok);
        Assert.Contains(renamed.Issues, i => i.Code == SkillIssueCode.NameNotFolder && i.Severity == SkillSeverity.Warning && i.Detail == "contract-review-main");
        Assert.Equal("contract-review", renamed.Document!.Name); // the skill is kept under its own name
    }

    [Fact]
    public async Task An_existing_skill_of_the_same_name_is_replaced_only_when_the_person_confirms()
    {
        using var rig = new WorkshopRig();
        await rig.Workshop.SaveAsync(WorkshopRig.Doc(description: "The old one."));
        var analysis = await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd)), "x.zip");

        var unconfirmed = await rig.Workshop.CommitImportAsync(analysis, replaceExisting: false);

        Assert.True(analysis.WouldReplace);
        Assert.Equal(SkillSaveStatus.Exists, unconfirmed.Status);
        Assert.Equal("The old one.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);

        var confirmed = await rig.Workshop.CommitImportAsync(analysis, replaceExisting: true);

        Assert.Equal(SkillSaveStatus.Saved, confirmed.Status);
        Assert.Equal("Reviews a contract clause by clause.", (await rig.Workshop.ReadAsync("contract-review")).Document!.Description);
    }

    [Theory]
    [InlineData("../evil/SKILL.md")]
    [InlineData("contract-review/../../evil.md")]
    [InlineData("contract-review/references/../../../evil.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.md")]
    [InlineData("C:\\Windows\\x.md")]
    [InlineData("contract-review\\SKILL.md")]
    [InlineData("contract-review/a\\..\\b.md")]
    [InlineData("contract-review//x.md")]
    public async Task A_zip_entry_that_escapes_or_is_absolute_refuses_the_whole_zip(string hostile)
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("contract-review/SKILL.md", GoodSkillMd), Text(hostile, "pwned"));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "hostile.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.UnsafePath);
        Assert.Null(analysis.Document);
        Assert.False(File.Exists(Path.Combine(rig.Root, "evil.md")));
        Assert.Equal(SkillSaveStatus.Refused, (await rig.Workshop.CommitImportAsync(analysis, true)).Status);
        Assert.False(Directory.Exists(rig.Folder));
    }

#if NET
    [Fact]
    public async Task A_link_entry_refuses_the_zip()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(entry =>
        {
            if (entry.FullName.EndsWith("link.md", StringComparison.Ordinal))
            {
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // a unix symbolic link, mode 0777
            }
        }, Text("contract-review/SKILL.md", GoodSkillMd), Text("contract-review/link.md", "/etc/passwd"));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "link.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.LinkEntry);
    }
#endif

    [Fact]
    public async Task An_oversize_file_in_a_zip_refuses_it_without_expanding_it()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("SKILL.md", GoodSkillMd), ("references/big.md", new byte[SkillLimits.FileMaxBytes + 1]));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "big.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.FileTooLarge);
    }

    [Fact]
    public async Task A_zip_bomb_is_refused_by_its_ratio_and_its_expanded_total()
    {
        using var rig = new WorkshopRig();
        // 5 files of 1 MiB of zeros: each is fine alone by size, but they compress about 1000:1 and expand to 5 MiB + 1.
        var entries = new List<(string, byte[])> { Text("SKILL.md", GoodSkillMd) };
        entries.AddRange(Enumerable.Range(0, 6).Select(i => ("references/zeros" + i + ".md", new byte[SkillLimits.FileMaxBytes])));
        var bomb = Zip(entries.ToArray());
        Assert.True(bomb.Length < 50_000);

        var analysis = await rig.Workshop.AnalyzeZipAsync(bomb, "bomb.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.ZipBomb, SkillIssueCode.TotalTooLarge);
    }

    [Fact]
    public async Task A_single_highly_compressed_file_is_a_bomb_even_when_it_is_under_the_size_limit()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("SKILL.md", GoodSkillMd), ("references/zeros.md", new byte[900 * 1024]));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "zeros.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.ZipBomb);
    }

    [Fact]
    public async Task A_zip_with_too_many_files_is_refused()
    {
        using var rig = new WorkshopRig();
        var entries = new List<(string, byte[])> { Text("SKILL.md", GoodSkillMd) };
        entries.AddRange(Enumerable.Range(0, 120).Select(i => Text("references/f" + i + ".md", "x " + i)));

        var analysis = await rig.Workshop.AnalyzeZipAsync(Zip(entries.ToArray()), "many.zip");

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.TooManyFiles);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_zip_and_a_zip_without_SKILL_md_are_refused()
    {
        using var rig = new WorkshopRig();

        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(Encoding.UTF8.GetBytes("this is not a zip"), "x.zip"), SkillIssueCode.NotAZip);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(Zip(Text("README.md", "hi")), "x.zip"), SkillIssueCode.SkillMdMissing);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(new byte[SkillLimits.ZipMaxBytes + 1], "x.zip"), SkillIssueCode.FileTooLarge);
    }

    [Fact]
    public async Task Unknown_SKILL_md_keys_are_refused_with_the_key_named_and_nothing_is_written()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("SKILL.md", "---\nname: contract-review\ndescription: x\nversion: 3\nhooks: run\n---\nBody\n"));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "keys.zip");

        Assert.NotNull(analysis.Document);
        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.UnknownKey);
        Assert.Equal(new[] { "version", "hooks" }, analysis.Issues.Where(i => i.Code == SkillIssueCode.UnknownKey).Select(i => i.Detail));
        Assert.Equal(SkillSaveStatus.Refused, (await rig.Workshop.CommitImportAsync(analysis, true)).Status);
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Theory]
    [InlineData(0x200B)]
    [InlineData(0x2060)]
    [InlineData(0xFEFF)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    public async Task Invisible_characters_in_a_zip_refuse_it_in_the_description_the_body_and_a_reference(int codePoint)
    {
        using var rig = new WorkshopRig();
        var hidden = char.ConvertFromUtf32(codePoint);
        var attempts = new[]
        {
            Zip(Text("SKILL.md", "---\nname: contract-review\ndescription: Reviews" + hidden + " it.\n---\nBody\n")),
            Zip(Text("SKILL.md", "---\nname: contract-review\ndescription: Reviews it.\n---\nBody ignore" + hidden + " previous\n")),
            Zip(Text("SKILL.md", GoodSkillMd), Text("references/a.md", "Fine" + hidden + " text")),
        };

        foreach (var zip in attempts)
        {
            var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "hidden.zip");

            AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.InvisibleCharacter);
        }
    }

    [Fact]
    public async Task An_analysis_with_an_error_is_never_committed_even_when_its_document_would_pass()
    {
        using var rig = new WorkshopRig();
        var analysis = await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd), Text("faden.yaml", "priority: 999\n")), "y.zip");
        Assert.False(analysis.Ok);

        var committed = await rig.Workshop.CommitImportAsync(analysis, replaceExisting: true);

        Assert.Equal(SkillSaveStatus.Refused, committed.Status);
        Assert.Contains(committed.Issues, i => i.Code == SkillIssueCode.FadenYamlUnreadable);
        Assert.False(Directory.Exists(rig.Folder), "a refused import wrote something");
    }

    [Fact]
    public async Task A_script_in_a_zip_and_an_unreadable_faden_yaml_refuse_it()
    {
        using var rig = new WorkshopRig();

        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd), Text("scripts/run.py", "print(1)")), "s.zip"), SkillIssueCode.ScriptFile);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd), Text("faden.yaml", "priority: 999\n")), "y.zip"), SkillIssueCode.FadenYamlUnreadable);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeZipAsync(Zip(Text("SKILL.md", GoodSkillMd), Text("faden.yaml", "a: &x 1\nb: *x\n")), "y.zip"), SkillIssueCode.FadenYamlUnreadable);
    }

    [Fact]
    public async Task Junk_entries_of_a_zip_made_on_a_Mac_are_left_out()
    {
        using var rig = new WorkshopRig();
        var zip = Zip(Text("contract-review/SKILL.md", GoodSkillMd), Text("__MACOSX/contract-review/._SKILL.md", "junk"), Text("contract-review/.DS_Store", "junk"));

        var analysis = await rig.Workshop.AnalyzeZipAsync(zip, "mac.zip");

        Assert.True(analysis.Ok);
        Assert.Empty(analysis.Document!.Resources);
    }

    [Fact]
    public async Task A_folder_is_imported_through_the_same_checks_and_nothing_is_written_by_the_analysis()
    {
        using var rig = new WorkshopRig();
        var source = Path.Combine(rig.Root, "downloads", "contract-review");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        File.WriteAllText(Path.Combine(source, "SKILL.md"), GoodSkillMd);
        File.WriteAllText(Path.Combine(source, "references", "tone.md"), "Warm.");

        var analysis = await rig.Workshop.AnalyzeImportAsync(source);

        Assert.True(analysis.Ok);
        Assert.Equal("references/tone.md", analysis.Document!.Resources.Single().Path);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.Equal(SkillSaveStatus.Saved, (await rig.Workshop.CommitImportAsync(analysis, false)).Status);
    }

    [Fact]
    public async Task A_hostile_folder_is_refused_a_script_an_unknown_key_a_hidden_character_and_a_missing_SKILL_md()
    {
        using var rig = new WorkshopRig();
        var hostile = Path.Combine(rig.Root, "downloads", "hostile");
        Directory.CreateDirectory(Path.Combine(hostile, "scripts"));
        File.WriteAllText(Path.Combine(hostile, "SKILL.md"), "---\nname: hostile\ndescription: A\u200bB\nplan: evil\n---\nBody\n");
        File.WriteAllText(Path.Combine(hostile, "scripts", "x.ps1"), "Remove-Item *");
        var empty = Path.Combine(rig.Root, "downloads", "empty");
        Directory.CreateDirectory(empty);

        var analysis = await rig.Workshop.AnalyzeImportAsync(hostile);

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.ScriptFile, SkillIssueCode.UnknownKey, SkillIssueCode.InvisibleCharacter);
        var codes = analysis.Issues.Select(i => i.Code).ToList();
        Assert.Contains(SkillIssueCode.ScriptFile, codes);
        Assert.Contains(SkillIssueCode.UnknownKey, codes);
        Assert.Contains(SkillIssueCode.InvisibleCharacter, codes);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeImportAsync(empty), SkillIssueCode.SkillMdMissing);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeImportAsync(Path.Combine(rig.Root, "missing")), SkillIssueCode.ReadFailed);
    }

#if NET
    [Fact]
    public async Task A_folder_that_holds_a_link_is_refused()
    {
        using var rig = new WorkshopRig();
        var source = Path.Combine(rig.Root, "downloads", "linked");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "SKILL.md"), GoodSkillMd);
        var target = Path.Combine(rig.Root, "secret.txt");
        File.WriteAllText(target, "secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(source, "link.md"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // this account may not make links (Windows without developer mode)
        }

        var analysis = await rig.Workshop.AnalyzeImportAsync(source);

        AssertRefusedAndNothingWritten(rig, analysis, SkillIssueCode.LinkEntry);
    }
#endif

    [Fact]
    public async Task A_zip_file_on_disk_is_imported_and_an_oversize_one_is_refused_before_it_is_read()
    {
        using var rig = new WorkshopRig();
        Directory.CreateDirectory(rig.Root);
        var good = Path.Combine(rig.Root, "good.zip");
        File.WriteAllBytes(good, Zip(Text("contract-review/SKILL.md", GoodSkillMd)));
        var huge = Path.Combine(rig.Root, "huge.zip");
        using (var stream = File.Create(huge))
        {
            stream.SetLength(SkillLimits.ZipMaxBytes + 1);
        }

        Assert.True((await rig.Workshop.AnalyzeImportAsync(good)).Ok);
        AssertRefusedAndNothingWritten(rig, await rig.Workshop.AnalyzeImportAsync(huge), SkillIssueCode.FileTooLarge);
    }

    [Fact]
    public async Task Export_then_import_gives_the_same_skill_back()
    {
        using var rig = new WorkshopRig();
        var document = WorkshopRig.Doc() with
        {
            License = "MIT",
            Extension = new SkillExtensionFields { Keywords = new[] { "clause" }, Title = "Contract review", Priority = 60 },
            Resources = new[] { SkillFile.FromText("references/tone.md", "Warm."), new SkillFile("assets/pic.bin", new byte[] { 1, 2, 3, 0, 255 }) },
        };
        await rig.Workshop.SaveAsync(document);

        var zip = await rig.Workshop.ExportAsync("contract-review");

        Assert.NotNull(zip);
        using (var archive = new ZipArchive(new MemoryStream(zip!)))
        {
            Assert.Equal(new[] { "contract-review/SKILL.md", "contract-review/assets/pic.bin", "contract-review/faden.yaml", "contract-review/references/tone.md" },
                archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        }
        using var other = new WorkshopRig();
        var analysis = await other.Workshop.AnalyzeZipAsync(zip!, "export.zip");
        Assert.True(analysis.Ok, string.Join(", ", analysis.Issues.Select(i => i.Code)));
        Assert.Empty(analysis.Issues);
        await other.Workshop.CommitImportAsync(analysis, false);
        var back = (await other.Workshop.ReadAsync("contract-review")).Document!;

        Assert.Equal((document.Name, document.Description, "MIT"), (back.Name, back.Description, back.License));
        Assert.Equal(document.Instructions.Trim(), back.Instructions.Trim());
        Assert.Equal(("Contract review", 60), (back.Extension.Title, back.Extension.Priority));
        Assert.Equal(new[] { "clause" }, back.Extension.Keywords);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255 }, back.Resources.Single(r => r.Path == "assets/pic.bin").Bytes);
        Assert.Equal("Warm.", back.Resources.Single(r => r.Path == "references/tone.md").Text);
    }

    [Fact]
    public async Task A_bundled_skill_is_exported_too_and_a_missing_one_is_null()
    {
        using var rig = new WorkshopRig();

        var zip = await rig.Workshop.ExportAsync("slide-design");

        Assert.NotNull(zip);
        using (var archive = new ZipArchive(new MemoryStream(zip!)))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "slide-design/SKILL.md");
            Assert.Contains(archive.Entries, e => e.FullName == "slide-design/layouts");
        }
        Assert.Null(await rig.Workshop.ExportAsync("not-there"));
        Assert.Null(await rig.Workshop.ExportAsync("../x"));
    }
}
