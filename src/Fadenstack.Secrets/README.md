# Fadenstack.Secrets

Encryption at rest for the [Fadenstack](https://fadenstack.com) SDK on macOS and Linux. Sessions (transcripts),
approvals and sign-in tokens are encrypted with AES-256-GCM; the data key is kept in the OS key store (macOS Keychain,
Linux Secret Service such as GNOME Keyring or KWallet) through
[Microsoft.Identity.Client.Extensions.Msal](https://www.nuget.org/packages/Microsoft.Identity.Client.Extensions.Msal).
On Windows the SDK already encrypts with DPAPI; this package changes nothing there.

```bash
dotnet add package Fadenstack.Secrets
```

```csharp
using Fadenstack.Secrets;

// once at start-up, before any store, token store or agent is created
FadenstackSecrets.UseOsKeyStore(loggerFactory.CreateLogger("Fadenstack"));
```

- Data written earlier without encryption (owner-only files) is still read, and encrypted on its next write.
- Without a working key store (a headless Linux with no Secret Service) the call returns `false` and the owner-only
  files stay. The agent then keeps session transcripts in memory only (it logs a warning); approvals and tokens keep
  their owner-only files.
- Targets `net8.0` only: .NET Framework hosts are Windows and use DPAPI.

- Source and issues: <https://github.com/fadenstack/fadenstack-sdk-dotnet>
- Licence: Apache-2.0 (LICENSE and NOTICE are in the package)
