using System.Globalization;
using Fadenstack.LocalTools;
using Fadenstack.UI.Tools;
using Xunit;
using static Fadenstack.UI.Tests.ToolsView.ToolsPageTests;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>A .cmd or .bat program is not started when an argument holds a character cmd.exe reads as a command (sdk 0.3.1): the card says so, in each language.</summary>
public class ToolsStartRefusalTests
{
    private static FakeLocalToolsCatalog Refused(LocalServerState state = LocalServerState.NotAllowed)
    {
        var catalog = new FakeLocalToolsCatalog();
        catalog.Servers.Add(new LocalServerInfo
        {
            Label = "batch", Where = "tool.cmd", IsProgram = true, Enabled = true, State = state, Origin = "typed", CallTimeoutSeconds = 60,
            StartRefusal = LocalStartRefusal.ShellCharacter,
        });
        return catalog;
    }

    [Theory]
    [InlineData("en", "Not started: an argument holds a character that Windows would run as a command (& | < > ^ % ! \" ( ) or a line break). A .cmd or .bat program cannot take it. Remove it, or point to the program itself.")]
    [InlineData("de", "Nicht gestartet: Ein Argument enthält ein Zeichen, das Windows als Befehl ausführen würde (& | < > ^ % ! \" ( ) oder ein Zeilenumbruch). Ein .cmd- oder .bat-Programm verträgt es nicht. Entfernen Sie es oder verweisen Sie auf das Programm selbst.")]
    [InlineData("fr", "Non démarré : un argument contient un caractère que Windows exécuterait comme une commande (& | < > ^ % ! \" ( ) ou un saut de ligne). Un programme .cmd ou .bat ne peut pas l’accepter. Supprimez-le, ou indiquez le programme lui-même.")]
    [InlineData("es", "No se inició: un argumento contiene un carácter que Windows ejecutaría como un comando (& | < > ^ % ! \" ( ) o un salto de línea). Un programa .cmd o .bat no puede admitirlo. Quítelo o indique el propio programa.")]
    [InlineData("zh-Hans", "未启动：参数中含有 Windows 会当作命令执行的字符（& | < > ^ % ! \" ( ) 或换行）。.cmd 或 .bat 程序不能接受这些字符。请删除它们，或直接指向程序本身。")]
    public void The_refused_start_has_its_own_sentence_in_the_five_languages(string language, string text) =>
        Assert.Equal(text, UiStrings.For(CultureInfo.GetCultureInfo(language)).McpStartShellCharacter);

    [Fact]
    public void The_card_of_a_refused_program_says_so_as_a_warning()
    {
        using var page = Page(Refused());

        var card = Server(page, "batch");

        Assert.Equal((UiStrings.For(CultureInfo.GetCultureInfo("en")).McpStartShellCharacter, ServerStatusKind.Warning), (card.StatusText, card.StatusKind));
    }
}
