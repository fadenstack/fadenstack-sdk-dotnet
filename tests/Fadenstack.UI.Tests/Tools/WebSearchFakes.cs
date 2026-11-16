using Fadenstack.Auth;
using Fadenstack.WebSearch;

namespace Fadenstack.UI.Tests.ToolsView;

/// <summary>
/// A key store that "encrypts" by reversing the bytes (or does not, where <c>encrypts</c> is false) and remembers what it was given, so a
/// test can say what reached the key store. Not a protector for anything else.
/// </summary>
internal sealed class RecordingProtector : ISecretProtector
{
    private readonly bool _encrypts;

    public RecordingProtector(bool encrypts = true) => _encrypts = encrypts;

    /// <summary>The plain text of every key that was given to the key store.</summary>
    public List<string> Protected { get; } = new();

    public bool Encrypts => _encrypts;

    public byte[] Protect(byte[] data)
    {
        Protected.Add(System.Text.Encoding.UTF8.GetString(data));
        var copy = (byte[])data.Clone();
        Array.Reverse(copy);
        return copy;
    }

    public byte[] Unprotect(byte[] data)
    {
        var copy = (byte[])data.Clone();
        Array.Reverse(copy);
        return copy;
    }
}

/// <summary>A person's web search store in a folder of its own that is deleted with the fixture.</summary>
internal sealed class WebSearchFixture : IDisposable
{
    public WebSearchFixture(bool encrypts = true)
    {
        Folder = Path.Combine(Path.GetTempPath(), "faden-websearch-" + Guid.NewGuid().ToString("N"));
        Protector = new RecordingProtector(encrypts);
        Store = new WebSearchStore(Folder, Protector);
    }

    public string Folder { get; }

    public RecordingProtector Protector { get; }

    public WebSearchStore Store { get; }

    /// <summary>A key is kept and Brave is the choice (what a person who set it up earlier has).</summary>
    public WebSearchFixture WithBrave(string key = "brave-key-123")
    {
        Store.Save(new WebSearchSettings { Provider = WebSearchProvider.Brave });
        Store.SetBraveKey(key);
        return this;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // a temp folder that stays is not a failure
        }
    }
}
