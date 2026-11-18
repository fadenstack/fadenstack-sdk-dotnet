using Fadenstack.Agents.Audit;
using Xunit;

namespace Fadenstack.Agents.Tests;

/// <summary>
/// The CSV cell of the export of the record of tool calls. The cases are the shared table: the same inputs and outputs are in the
/// TypeScript tests (<c>csvCell</c> and the UI's <c>csvField</c>). ASVS 5.0 V1 (output encoding, 1.2.10).
/// </summary>
public class AuditCsvCellTests
{
    public static TheoryData<string, string> SharedCases => new()
    {
        { "plain", "plain" },
        { "", "" },
        { "a=b", "a=b" },
        { "a,b", "\"a,b\"" },
        { "a;b", "\"a;b\"" },
        { "=cmd", "'=cmd" },
        { "a;=b", "\"a;'=b\"" },
        { "=1;=2", "\"'=1;'=2\"" },
        { "x,=y", "\"x,'=y\"" },
        { "x;-5", "\"x;'-5\"" },
        { "a;;@b", "\"a;;'@b\"" },
        { "say \"hi\"; ok", "\"say \"\"hi\"\"; ok\"" },
        { "\"a\";=b", "\"\"\"a\"\";'=b\"" },
        { "two\nlines", "\"two\nlines\"" },
    };

    [Theory]
    [MemberData(nameof(SharedCases))]
    public void Cell_follows_the_shared_table(string input, string expected) => Assert.Equal(expected, AuditExport.Cell(input));
}
