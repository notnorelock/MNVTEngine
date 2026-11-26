# Local data protection

`MNVTEngine.Security.LocalProtection` (core module, always shipped) keeps small secrets a game stores on the player's
machine, such as a session token or a remembered sign-in, unreadable outside this Windows account on this computer: a
copied file, another user or another machine gets nothing. It is Windows DPAPI for the current user.

```csharp
using MNVTEngine.Security;

string stored = LocalProtection.ProtectString(token, "MyGame.SessionTokens");
string? token = LocalProtection.UnprotectString(stored, "MyGame.SessionTokens"); // null: sign in again
```

- **Purpose.** Each purpose mixes in its own entropy, so data protected for one purpose does not unprotect for another.
  Keep it constant for the data's lifetime.
- **Unreadable is missing.** `Unprotect`/`UnprotectString` return null for damaged data, another user's or machine's,
  another purpose, or text that was never protected; treat it as "not signed in", never crash on it.
- **Not on Windows.** `IsAvailable` is false and `Protect` throws `PlatformNotSupportedException`; decide what to do
  without it (keep nothing and ask again) instead of writing the secret in the clear.
- It protects data at rest against other accounts and copies, not against code running as the same user.
- Not for game content (that is the [virtual file system](../architecture/virtual-filesystem.md)) or large files.

Tests: `Security.Tests` (`LocalProtectionTests`); architecture rule `Security_HasNoEngineDependencies_AndOnlyDpapi`.
