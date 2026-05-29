# ZwiftClickV2-Bridge — Agent Instructions

Reverse-engineering bridge to connect a **Zwift Click V2** (2025) controller to MyWoosh via keyboard emulation on Windows.  
Status and context: [README.md](README.md) | [REVERSE_ENGINEERING_V2.md](docs/REVERSE_ENGINEERING_V2.md) | [CONTRIBUTING.md](CONTRIBUTING.md)

---

## Build & Test

```bash
# Build
dotnet build

# Run (src project — all modes require Windows BLE)
dotnet run --project src -- --fuzz-v2        # V2 header discovery fuzzer
dotnet run --project src -- --probe          # 6-format handshake prober
dotnet run --project src -- --fuzz           # exhaustive fuzzer (~15 variants)
dotnet run --project src -- --hot-pair       # BikeControl-style handshake
dotnet run --project src -- --diagnose-crypto
dotnet run --project src -- --bridge

# Unit tests (no device needed)
dotnet test
```

Tests live in `tests/` (xUnit, `[Fact]` only — no `[Theory]`). Tests in `src/Test/` are **live device harnesses**, not unit tests.

---

## Architecture

| Folder | Namespace suffix | Responsibility |
|--------|-----------------|----------------|
| `BLE/` | `.BLE` | Windows BLE stack via `Windows.Devices.Bluetooth` WinRT. Zwift service UUID `0xFC82`, characteristics CH02–CH102. |
| `Bridge/` | `.Bridge` | Top-level orchestrators (`ClickV2Bridge`, `HotPairBridge`). `KeyboardEmulator` calls `SendInput`/`user32.dll`. Experimental probers: `UnlockProber`, `PacketDecryptor`. |
| `Crypto/` | `.Crypto` | `ZapCrypto` (HKDF + AES-256-CCM primitive). `ZPEncryptionV2` (stateful tx/rx counter wrapper). `ZPEncryptionFactory` detects V1 vs V2 from handshake suffix bytes. |
| `Protocol/` | `.Protocol` | ZOP framing (`ZopSequencer`), typed messages (`Messages/`), handshake fuzzers/parsers. `ZwiftClickProtocol` is a **stub** (all methods `throw NotImplementedException`). |
| `Logging/` | `.Logging` | `StructuredLogger` → `logs/session_YYYYMMDD_HHmmss.json`, thread-safe. |
| `src/Test/` | `.Test` | Live device test harnesses (require physical Zwift Click hardware). |

---

## Crypto Details

- **Curve**: P-256 (secp256r1). Public keys: 65 bytes (`0x04 ‖ X[32] ‖ Y[32]`) externally; `ZPEncryptionV2.NormalizePublicKey` accepts both 64- and 65-byte forms.
- **HKDF**: SHA-256, `info = "handshake data"`, `salt = devicePubKey[64] ‖ localPubKey[64]` (128 bytes, no `0x04` prefix).  
  Derives 36 bytes → first 32 = AES key, last 4 = IV base.
- **Cipher**: AES-256-CCM, 4-byte tag, 8-byte nonce (`IvBase[4] ‖ counter[4B big-endian]`).
- **Counter**: auto-incremented per message; `ZopSequencer` prepends a 4-byte LE sequence number to each frame.

---

## Active Research Blockers

1. **V2 handshake rejection** — all known header variants rejected. The real Zwift app resolves an `auth challenge` via `INetworkService` (bearer `access_token`). This HTTP exchange is not yet captured. See [REVERSE_ENGINEERING_V2.md](docs/REVERSE_ENGINEERING_V2.md).
2. **`ZwiftClickProtocol` stub** — `PerformHandshakeAsync`, `ProcessIncomingData`, `SendCommandAsync` are `TODO`. Implementation blocked on (1).
3. **ZOP post-handshake messages** — encrypted with AES-128-GCM; Protobuf schemas not yet extracted.

---

## Conventions

- **Language**: source comments and `Console.WriteLine` messages are in Spanish.
- **Naming**: `_camelCase` private fields; `PascalCase` methods/classes; async methods always end with `Async`.
- **Nullability**: nullable enabled project-wide; never use `!` suppressor — callers check `== null`.
- **Target framework**: `net8.0-windows10.0.19041.0` (WinRT BLE APIs require this TFM).
- **NuGet**: `Google.Protobuf 3.35.0`, `Portable.BouncyCastle 1.9.0` (BCL crypto preferred for primitives).
- **Error handling**: guard clauses throw typed exceptions; BLE failures return `null`/`false` rather than propagating. User-facing errors print `❌ ...` to console.
- **Resources**: implement `IDisposable` on classes that own BLE resources; use `IZPEncryption` interface for crypto injection (testability).

---

## Key Files

- `src/Program.cs` — all CLI entry points, device name argument pattern
- `src/Crypto/ZapCrypto.cs` — HKDF + AES-256-CCM primitive
- `src/Crypto/ZPEncryptionV2.cs` — stateful crypto adapter
- `src/Protocol/ZopSequencer.cs` — wire framing (sequence numbers + encrypt/decrypt)
- `src/Protocol/HandshakeFuzzerV2.cs` — edit `HEADERS_TO_TEST` to try new 7-byte headers
- `tests/ZPEncryptionTests.cs` — reference for test patterns
- `logs/` — JSON session logs from fuzzer/prober runs
