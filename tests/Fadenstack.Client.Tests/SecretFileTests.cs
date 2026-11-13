using Fadenstack.Auth;
using Xunit;

namespace Fadenstack.Client.Tests;

/// <summary>
/// <see cref="SecretProtection.WriteOwnerOnly(string, byte[])"/>: the file every store keeps a secret in (tokens, stored
/// approvals). sdk/51 I00 #3 (I-2, N2).
/// </summary>
public sealed class SecretFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faden-secret-" + Guid.NewGuid());

    public SecretFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, true);

    private string File_ => Path.Combine(_directory, "x.token");

    [Fact]
    public async Task Writers_at_once_never_clash_and_never_tear_the_file()
    {
        // Each writer writes its own letter, large enough that a torn file would mix two.
        var payloads = Enumerable.Range(0, 6).Select(i => Enumerable.Repeat((byte)('a' + i), 128 * 1024).ToArray()).ToArray();
        var torn = 0;
        using var stop = new CancellationTokenSource();
        // A reader that never pauses, reading the way the stores do.
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var read = SecretProtection.ReadShared(File_);
                    if (!payloads.Any(p => p.AsSpan().SequenceEqual(read)))
                    {
                        Interlocked.Increment(ref torn);
                    }
                }
                catch (FileNotFoundException)
                {
                    // before the first write
                }
            }
        });

        try
        {
            var writers = payloads.Select(p => Task.Run(() =>
            {
                for (var n = 0; n < 15; n++)
                {
                    SecretProtection.WriteOwnerOnly(File_, p);
                }
            })).ToArray();
            await Task.WhenAll(writers); // none threw
        }
        finally
        {
            stop.Cancel();
            await reader;
        }

        Assert.Equal(0, torn);
        var final = File.ReadAllBytes(File_);
        Assert.Contains(payloads, p => p.AsSpan().SequenceEqual(final));
        Assert.Equal(new[] { File_ }, Directory.GetFiles(_directory)); // no temporary file left behind
    }

    [Fact]
    public void The_file_is_made_owner_only_while_it_is_still_empty()
    {
        long? lengthWhenRestricted = null;
        SecretProtection.WriteOwnerOnly(File_, "secret"u8.ToArray(), file => lengthWhenRestricted = new FileInfo(file).Length);

        Assert.Equal(0, lengthWhenRestricted);
        Assert.Equal("secret"u8.ToArray(), File.ReadAllBytes(File_));
    }

    [Fact]
    public void When_the_file_cannot_be_made_owner_only_nothing_is_written_and_the_old_file_stays()
    {
        File.WriteAllBytes(File_, "old"u8.ToArray());

        Assert.Throws<IOException>(() => SecretProtection.WriteOwnerOnly(File_, "secret"u8.ToArray(),
            _ => throw new IOException("The mode could not be set.")));

        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(File_));
        Assert.Equal(new[] { File_ }, Directory.GetFiles(_directory));
    }

    [Fact]
    public void The_error_names_the_kind_of_failure_never_the_path()
    {
        var error = SecretProtection.OwnerOnlyFailed("errno 1");

        Assert.IsAssignableFrom<IOException>(error);
        Assert.Contains("errno 1", error.Message);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), error.Message);
    }

    [Fact]
    public void On_macOS_and_Linux_the_file_is_the_owners_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the user profile is the user's, and DPAPI encrypts
        }
        SecretProtection.WriteOwnerOnly(File_, "secret"u8.ToArray());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
        SecretProtection.WriteOwnerOnly(File_, "again"u8.ToArray()); // replacing keeps it so
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(File_));
    }
}
