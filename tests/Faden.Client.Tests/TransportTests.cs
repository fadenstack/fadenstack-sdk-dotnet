using System.Security.Authentication;
using Faden.Http;
using Xunit;

namespace Faden.Client.Tests;

public class TransportTests
{
    [Fact]
    public void Modern_TLS_is_1_2_and_1_3_where_the_runtime_knows_1_3()
    {
        // .NET Framework hosts (Office, EPLAN) get this on the SDK's handler instead of their legacy SSL 3 / TLS 1.0.
        var protocols = FadenTransport.ModernTls();
        Assert.True(protocols.HasFlag(SslProtocols.Tls12));
        Assert.Equal(Enum.IsDefined(typeof(SslProtocols), (SslProtocols)12288), protocols.HasFlag((SslProtocols)12288));
#pragma warning disable SYSLIB0039 // the legacy protocols named only to say they are not chosen
        Assert.False(protocols.HasFlag(SslProtocols.Tls) || protocols.HasFlag(SslProtocols.Tls11));
#pragma warning restore SYSLIB0039
    }
}
