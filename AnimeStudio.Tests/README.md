# AnimeStudio.Tests

Regression tests for the encrypted Arknights: Endfield VFS container
(`AnimeStudio/EndfieldBundleKeys.cs`, `AnimeStudio/VFSFile.cs`,
`AnimeStudio/PartialVfsStream.cs`).

Run them with either target framework:

```powershell
dotnet run --project AnimeStudio.Tests/AnimeStudio.Tests.csproj -f net9.0
dotnet run --project AnimeStudio.Tests/AnimeStudio.Tests.csproj -f net10.0
```

The runner exits with a non-zero code when a test fails, so it can gate CI.

## What is covered

| Area | Tests |
| --- | --- |
| Key descriptor unpacking | AES-256-CBC unwrap, descriptor XOR, lowercase/whitespace, malformed input, bad padding, corrupted ciphertext |
| Block stream | Captured UnityPlayer fixtures, independent reference vectors, offset windows, non-16-aligned starts, 16-byte boundary behaviour, large offsets, key length validation |
| Partial streams | Reads before/after/inside unavailable ranges, exact boundaries, `ReadByte`, seeking, multiple ranges, disposal |
| Key store | 16-byte validation, replacement, copy semantics |
| VFS container | Synthetic bundles with blocks at unaligned output offsets, missing key quarantine, corrupt block quarantine, truncated payload |

## Fixtures and references

* `Data/block-zero.*` and `Data/block-nonzero-offset.*` are golden vectors
  captured from the verified UnityPlayer stream implementation.
* `Data/stream-vectors.json` is produced by `tools/endfield_stream_reference.py`,
  an independent Python implementation of the same stream. It reads the S-box
  straight out of `CryptoHelper.cs`, so the table cannot drift.
* `ReferenceEndfieldStream.cs` is a second, independent C# implementation kept in
  a FIPS-197 `[row, column]` layout; the tests use it to build the synthetic
  bundles, so production code has to agree with a differently structured
  implementation rather than with itself.

Regenerate the vectors (and optionally re-check the captured fixtures):

```powershell
python tools/endfield_stream_reference.py
python tools/endfield_stream_reference.py --check
```

The cipher constants in `EndfieldBundleCrypto` are corroborated by the shipped
binary: each seed word appears in `UnityPlayer.dll` as the two 32-bit
immediates that are XORed together at runtime, and the 256-byte custom S-box
appears twice as a contiguous table.
