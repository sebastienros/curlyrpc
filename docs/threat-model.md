# CurlyRpc threat model

Status: initial assessment, October 2026, against `main` at `0c6f038`; statuses re-checked against
the code after #21. Findings marked **open** are not yet fixed. Each finding was reproduced with a proof
of concept against the current source, or confirmed by reading the code where noted.

## 1. Scope

CurlyRpc is a library, not a service. It parses bytes from a peer, dispatches them to application
code, and serializes results back. This model covers what the library itself must guarantee no
matter how it is hosted. It also lists what it deliberately leaves to the host.

In scope:

- Framing (`Framing/`), envelope parsing and dispatch (`JsonRpc.Internals.cs`)
- Method exposure (`AddLocalRpcTarget`, `AddLocalRpcMethod`)
- Streaming (`JsonRpc.Streaming.cs`, `Streaming/`)
- Cancellation, keep-alive and connection lifecycle
- The built-in authentication middleware and transport helpers (`Auth/`)
- Observability (`Diagnostics/`, `Logging/`)
- The proxy source generator (`CurlyRpc.SourceGenerator`)

Out of scope:

- Confidentiality and integrity of the transport. That's the host's job: TLS, pipe ACLs, socket
  permissions.
- The security of application handlers themselves.
- `System.Text.Json` and runtime vulnerabilities. They're patched with the .NET runtime.

## 2. Deployment assumptions

CurlyRpc was built for local host-to-CLI channels, such as the
[Aspire](https://github.com/dotnet/aspire) backchannel. There, both ends run as the same user and the
peer proves itself with a shared secret. **Default options assume a trusted peer.** For anything else,
the documented contract is `JsonRpcOptions.CreateHardened()` plus `HandshakeAuthenticationMiddleware`.
The threats below are rated against that hardened configuration unless stated otherwise. A threat
that also applies to default options is flagged.

## 3. Assets

| Asset | Why it matters |
| --- | --- |
| Host process availability (CPU, memory, thread pool) | A peer must not be able to stall or crash the process, or starve other connections. |
| Application methods and the data behind them | Only methods the developer intended may be invoked, and only after authentication. |
| The shared authentication secret | It authorizes every method on the connection. |
| Error and diagnostic content | Exception text, logs and traces can contain paths, SQL or secrets. |
| Integrity of the call/response correlation | A peer must not complete, cancel or redirect another caller's calls. |
| Observability backends | Peer input must not explode metric cardinality or flood logs. |

## 4. Actors

- **Untrusted, unauthenticated peer.** Anything that can reach the transport before the handshake.
  It can send arbitrary bytes.
- **Authenticated peer.** It has the secret, so it is trusted to call methods, but not to crash the
  process or reach methods the developer didn't intend to expose.
- **Malicious server.** The library is symmetric: a client calling a hostile server parses its
  responses, ids and streams through the same code.
- **Local user on the same machine.** Can try to reach a Unix socket or named pipe.

## 5. Trust boundaries and data flow

```
 peer bytes
     │
 ┌───▼──────────────────────────────┐  B1 transport (host-provided: TLS / pipe ACL / socket perms)
 │ Framing: header or newline       │  B2 framing: untrusted bytes → bounded frames
 │  Content-Length, size caps       │
 └───┬──────────────────────────────┘
     │ frame
 ┌───▼──────────────────────────────┐  B3 parse + admission: frame → JSON envelope(s)
 │ JsonDocument.Parse, batch split, │     (runs on the read loop, BEFORE authentication)
 │ id parsing, admission budget     │
 └───┬────────────────┬─────────────┘
     │ requests       │ responses / $/cancelRequest / $/ping  (bypass middleware)
 ┌───▼──────────────┐ │
 │ Inbound          │ │                B4 authorization: middleware (handshake)
 │ middleware       │ │
 └───┬──────────────┘ │
 ┌───▼──────────────────────────────┐  B5 dispatch: method name → reflected / typed handler
 │ Method table, param binding,     │     parameter deserialization into declared types
 │ streaming enumerators            │
 └───┬──────────────────────────────┘
     │ result / error
 ┌───▼──────────────────────────────┐  B6 egress: error scrubbing, serialization, write lock
 │ Serialize, write frame           │
 └──────────────────────────────────┘
   diagnostics: Activity / Meter / ILogger  (B7: peer-controlled strings leave the process)
```

What crosses B3 before B4 matters most. Framing, JSON parsing, batch splitting, **request-id
parsing**, `$/ping`, `$/cancelRequest` and response correlation all run before authentication. Each
of them must be cheap and bounded.

## 6. Threats and mitigations

Status key: ✅ mitigated · ⚠️ partially mitigated or opt-in · ❌ open.

### 6.1 Framing and parsing (B2, B3)

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| F-1 | Huge declared `Content-Length` forces a large allocation | ✅ (opt-in) | Rejected before buffering (`HeaderDelimitedMessageHandler.cs:69-72`). Needs `MaximumInboundMessageSize`, which is unlimited by default. |
| F-2 | Duplicate, negative, overflowing or garbage `Content-Length` (request smuggling) | ✅ | `HeaderDelimitedMessageHandler.cs:125-172`, with tests. |
| F-3 | Header block never terminated, so the buffer grows without bound | ✅ | `HeaderDelimitedMessageHandler.MaximumHeaderSize` (default 8 KiB) applies even when `MaximumMessageSize == 0` and throws `InvalidDataException`. The terminator search resumes from the last scanned offset. Fixed in #18. |
| F-4 | Unbounded newline-delimited line | ✅ (opt-in) | `NewlineDelimitedMessageHandler.cs:85-98`. |
| F-5 | Deeply nested JSON (stack, CPU) | ✅ | `JsonDocument`/STJ `MaxDepth` 64. Nested batches are rejected rather than recursed into. |
| F-6 | Huge numeric request `id` burns CPU | ✅ | Exponents are parsed as a bounded `long`, never `BigInteger`. Exponents beyond ±1e18 get an opaque raw-token key; zero still normalizes. A 1 MB id now normalizes in milliseconds. Fixed in #18. |
| F-7 | Partial-frame rescans (slow drip) | ✅ | Both scanners remember how far they have searched. Fixed in #18. |
| F-8 | Per-connection buffers retained at peak size | ✅ | Buffers over 1 MiB are released once the large frame is consumed. Fixed in #18. |
| F-9 | Size cap silently not applied | ⚠️ Info | Negative size and concurrency limits now throw `ArgumentOutOfRangeException` (the `JsonRpc` constructor validates every option, and `StreamMessageHandler.MaximumMessageSize` validates on set). **Still open:** `MaximumInboundMessageSize` is ignored when the caller passes their own handler (documented). |

### 6.2 Resource exhaustion and amplification (B3, B6)

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| R-1 | Request flood exhausts handlers or the thread pool | ✅ (opt-in) | `MaximumConcurrentRequests` throttle. |
| R-2 | Non-reading peer piles up queued work and replies | ✅ (opt-in) | Admission budget by count and bytes, held until the reply is written (`JsonRpc.Internals.cs:258-276`, `:391-398`). |
| R-3 | Response amplification | ⚠️ Low | Method-not-found echoes at most 128 characters of the method name, and `MaximumBatchSize` (1000 under `CreateHardened`, `0` = unlimited by default) rejects an oversized batch with one error before any element runs (#21). A request id longer than 256 characters (string length, or raw token length for a number) is rejected with Invalid Request and a `null` id under every configuration, so escaping can no longer inflate an echoed id. **Still open:** there is no outbound byte budget, so handler results and application error messages are unbounded. |
| R-4 | Too many open server streams | ✅ (opt-in) | `MaximumActiveEnumerations`. There is no idle expiry, so abandoned streams hold slots until disconnect. |
| R-5 | Malicious server streams forever to a client | ⚠️ Low | `InvokeAsyncEnumerable` loops until `finished`, including over empty batches. Only the consumer's cancellation stops it. |
| R-6 | **Permissive defaults** | ⚠️ | All limits default to unlimited and `ExposeExceptionDetails` to `true`. The README documents this and points to `CreateHardened`. Consider making hardened the default; see §8. |

### 6.3 Authentication and authorization (B4)

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| A-1 | Calling methods before authenticating | ✅ | Middleware gates every request and notification, including batch elements and `$/enumerator/*` (`JsonRpc.Internals.cs:595-637`). Only `$/ping`, `$/cancelRequest` and responses bypass it, and none of them reach app code. |
| A-2 | Middleware shared across connections lets one connection's auth apply to another | ✅ | Atomic, permanent binding (`HandshakeAuthenticationMiddleware.cs:71-78`), with tests. Fixed in #8. |
| A-3 | Timing attack on the token | ✅ | The SHA-256 digests of the candidate and expected tokens are compared with `FixedTimeEquals`, so a length mismatch no longer returns early. Only the digest of the secret is retained. |
| A-4 | Online token guessing within one batch, with handlers running after close is requested | ✅ | A failed handshake latches permanently in `HandshakeAuthenticationMiddleware`, rejecting every later request including a correct token. Dispatch stops once a close is requested (checked before middleware, before the handler, and between batch elements). Fixed in #21. |
| A-5 | Cross-connection cancellation, response spoofing or stream hijacking | ✅ | All correlation state is per `JsonRpc` instance. Responses only match `long` ids this side issued (`:516`). Enumerator tokens are sequential but scoped to the connection. |
| A-6 | Another local user reaches the socket or pipe | ⚠️ Low | `RestrictToCurrentUser` chmods after `bind()`, so there is a window, and it follows symlinks. The Windows guidance is incomplete: the default pipe DACL allows read for Everyone, so `PipeOptions.CurrentUserOnly` should be recommended. |

### 6.4 Method exposure and dispatch (B5)

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| D-1 | Reflection exposes unintended methods | ✅ | `AddLocalRpcTarget` excludes `object` members and their overrides (via `GetBaseDefinition`), compiler-generated and record-synthesized members, `IDisposable`/`IAsyncDisposable`/`IEquatable<T>` implementations, plus static, generic, special-name and non-public methods (`Dispatch/RpcMethodEligibility.cs`). `IncludeInheritedMethods` now defaults to `false`. Fixed in #20. |
| D-2 | `async void` handler crashes the process | ✅ | `async void` methods are skipped on targets, and rejected with `ArgumentException` for delegates and for `[JsonRpcMethod]`-marked methods. Fixed in #20. |
| D-3 | Silent overwrite on duplicate registration | ✅ | A duplicate name, including overloads, throws `ArgumentException`, and a rejected target registers nothing. Fixed in #20. |
| D-4 | Unbindable parameters (`ref`/`out`/pointer) registered | ✅ | Skipped on targets (`ref`/`out`/`in`, pointer, ref struct), and rejected for delegates and `[JsonRpcMethod]`-marked methods. Fixed in #20. |
| D-5 | Type-directed or polymorphic deserialization (gadget RCE) | ✅ | STJ only, into declared types. `error.data` stays a `JsonElement`. No `BinaryFormatter`, Newtonsoft `TypeNameHandling`, MessagePack or `ISerializable` exceptions. This is the main class that hit StreamJsonRpc and MessagePack-CSharp. |
| D-6 | Prototype or inherited-key lookup on the method table | ✅ | Ordinal `Dictionary<string, …>`. |

### 6.5 Cancellation, streaming and lifecycle

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| L-1 | Streaming iterator never observes cancellation | ✅ | Each enumeration takes ownership of its request's CTS (linked to the connection), which `RpcEnumerableResult` cancels on abort, completion or shutdown before waiting on its read gate, then disposes. Fixed in #21. |
| L-2 | Throwing cancellation callback kills the read loop or breaks Shutdown | ✅ | `$/cancelRequest` uses `CancelAsync` (callbacks off the read loop, failures observed). Other cancellations catch every exception. Shutdown always completes `Completion` and drains enumerators (try/finally), and `DisposeAsync` always disposes the handler. Fixed in #21. |
| L-3 | Read-loop fault leaves the transport open | ✅ | A read-loop failure now calls `RequestClose`, so the peer sees end-of-stream and in-flight handlers are canceled. Fixed in #21. |
| L-4 | Outbound call after connection loss hangs forever | ✅ | `InvokeCoreAsync` throws `ObjectDisposedException` after disposal, and re-checks shutdown after publishing the call, failing it with `ConnectionLostException`. Fixed in #21. |
| L-5 | Writers queued behind a stuck write hang after dispose | ✅ | The write lock is never disposed; disposal cancels a token that queued writers observe, so they fail with `ObjectDisposedException`. Fixed in #18. |
| L-6 | Duplicate inbound request ids | ✅ | A duplicate in-flight id is rejected with Invalid Request (`TryAdd`), and each request removes only its own registration (`TryRemove(KeyValuePair)`). Fixed in #21. |
| L-7 | Cancel of a request still queued behind the throttle is lost | ✅ | The CTS is registered before the throttle wait, and the wait observes it. Fixed in #21. |
| L-8 | Keep-alive silently disabled | ✅ | Any `JsonRpcException` reply (except `ConnectionLostException`) counts as alive. The whole ping, including the write, is raced against the timeout. Unexpected errors fault the connection instead of ending the loop, so an oversized `KeepAliveInterval` now faults rather than silently disabling keep-alive (it is still not validated up front). Fixed in #21. |
| L-9 | Unserializable `LocalRpcException.ErrorData` closes the connection | ✅ | Error data that fails to serialize (from `LocalRpcException` or a middleware decision) is dropped, and the reply becomes Internal Error, scrubbed unless `ExposeExceptionDetails` is on. The connection stays open. |

### 6.6 Information disclosure and observability (B6, B7)

| # | Threat | Status | Mitigation / gap |
| --- | --- | --- | --- |
| I-1 | Exception text leaks to the peer | ✅ (opt-in) | `ExposeExceptionDetails = false` scrubs every generic error path, including middleware, parameter deserialization and streaming faults. `JsonException` text is never echoed. Stack traces and type names are never sent (unlike LSP4J, pylsp and jsonrpc4j). The default is `true`. |
| I-2 | Parameter names in InvalidParams messages | ⚠️ Info | `"Missing required parameter 'ApiKey'"` is sent even when scrubbing is on. |
| I-3 | Peer-controlled method names become metric and trace tags, and Error logs | ✅ | The server Activity and `rpc.method` tag are created after method lookup, with unknown methods recorded as `_OTHER`. `rpc.jsonrpc.request_id` is capped at 128 characters. `JsonRpcLoggingBridge` logs method-not-found at Warning, and its message names `_OTHER` rather than the raw method. Fixed in #21. |
| I-4 | Peer-supplied trace context trusted | ⚠️ Low | Only with `PropagateTraceContext = true`. `traceparent` is validated by `ActivityContext.TryParse`, and an inbound `tracestate` over 512 characters (the W3C propagation minimum) is dropped. **Still open:** the peer controls the sampled flag. |
| I-5 | Exception messages recorded in local telemetry | ⚠️ Info | Activity status and the logging bridge record `exception.Message` even when wire scrubbing is on. |

### 6.7 Build and supply chain

| # | Threat | Status | Notes |
| --- | --- | --- | --- |
| S-1 | Vulnerable dependency | ✅ | One runtime dependency (`Microsoft.Extensions.Logging.Abstractions`). Central package management with transitive pinning. A vulnerable SourceLink dependency was bumped in #1. |
| S-2 | Generator emits broken or injected code | ✅ | Its inputs are the developer's own symbols, so there's no injection risk. Wire names are emitted with Roslyn's `SymbolDisplay.FormatLiteral`, so control characters in a `[JsonRpcMethod]` name no longer break compilation. |
| S-3 | Package or publish compromise | ⚠️ | `SECURITY.md` with private vulnerability reporting (#17) and NuGet trusted publishing (#16) are in place. **Still open:** no `NuGetAudit`/CodeQL gate, and Actions are pinned to tags rather than SHAs. See §8. |

## 7. Comparison with similar libraries

| Bug class (seen in) | CurlyRpc |
| --- | --- |
| Type-name / `ISerializable` deserialization ([StreamJsonRpc 568223e2](https://github.com/microsoft/vs-streamjsonrpc/commit/568223e2), MessagePack [CVE-2020-5234](https://github.com/advisories/GHSA-7q36-4xx7-xcxf), [CVE-2024-48924](https://github.com/advisories/GHSA-4qm4-8hg2-g2xm)) | ✅ Not possible by design (D-5) |
| No message size cap ([StreamJsonRpc #326](https://github.com/microsoft/vs-streamjsonrpc/issues/326), Go `x/tools` jsonrpc2, python-lsp-jsonrpc) | ✅ Opt-in cap (F-1) · ✅ header block (F-3, #18) |
| Deep nesting ([Newtonsoft CVE-2024-21907](https://github.com/advisories/GHSA-5crp-9r3c-p9vr)) | ✅ (F-5) |
| Quadratic big-integer parsing ([CPython CVE-2020-10735](https://github.com/advisories/GHSA-6jr7-xr67-mgxw)) | ✅ (F-6, #18) |
| Unbounded batches / response size ([go-ethereum #26681](https://github.com/ethereum/go-ethereum/pull/26681), [#27924](https://github.com/ethereum/go-ethereum/pull/27924), [jayson #215](https://github.com/tedeh/jayson/pull/215), [jsonrpsee #711](https://github.com/paritytech/jsonrpsee/pull/711)) | ⚠️ Count- and batch-bounded when hardened, echoed ids capped, no response byte budget (R-3) |
| Slow reader, unbounded outbound queue ([jsonrpsee #962](https://github.com/paritytech/jsonrpsee/pull/962)) | ✅ Admission holds until the write completes (R-2) |
| Unbounded subscriptions ([Besu GHSA-ffqr-pj4h-xq37](https://github.com/hyperledger/besu/security/advisories/GHSA-ffqr-pj4h-xq37)) | ✅ Opt-in cap, no idle expiry (R-4) |
| Pending calls not settled on close, handler context not canceled ([sourcegraph/jsonrpc2 #82](https://github.com/sourcegraph/jsonrpc2/pull/82), [#90](https://github.com/sourcegraph/jsonrpc2/pull/90)) | ✅ on close and for calls issued after close (L-4) |
| Cancellation races and hangs ([StreamJsonRpc #578](https://github.com/microsoft/vs-streamjsonrpc/issues/578), [tower-lsp #417](https://github.com/ebkalderon/tower-lsp/issues/417)) | ✅ Streaming cancellation (L-1), throwing callbacks (L-2) |
| Reflection exposing unintended methods (jsonrpc4j `ReflectionUtil`, json-rpc-2.0 [#64](https://github.com/shogowada/json-rpc-2.0/pull/64)) | ✅ Overrides, record members and `Dispose` excluded; inherited methods opt-in (D-1) |
| Stack traces or exception types in `error.data` (LSP4J, python-lsp-jsonrpc, jsonrpc4j) | ✅ Never sent; message scrubbing is opt-in (I-1) |
| Metric cardinality from peer input ([OTel Go CVE-2023-45142](https://github.com/advisories/GHSA-rcjv-mgp8-qvmr), [CVE-2023-47108](https://github.com/advisories/GHSA-8pgv-569h-w5rw)) | ✅ Unknown methods recorded as `_OTHER` (I-3) |
| Secrets in logs ([Besu GHSA-f429-4669-7rpx](https://github.com/hyperledger/besu/security/advisories/GHSA-f429-4669-7rpx)) | ⚠️ The token is never logged, but exception messages are (I-5) |
| Transport read errors become uncaught exceptions ([vscode-languageserver-node #870](https://github.com/microsoft/vscode-languageserver-node/issues/870)) | ✅ Surfaced through `Completion`, and the transport is closed (L-3) |

## 8. Recommendations

Fix first (pre-auth denial of service, or auth weaknesses):

1. **F-6** (done in #18): never feed peer input to `BigInteger`. Cap numeric-id token length (for example 64 bytes)
   and treat longer ids as invalid or unmatched. Add a regression test with a megabyte-long exponent.
2. **F-3** (done in #18): cap the header section (for example 8 KiB, even when `MaximumMessageSize == 0`), and
   resume the terminator search from the last scanned offset.
3. **A-4** (done in #21): latch a failed handshake in the middleware. In dispatch, stop processing (including the
   rest of a batch) once a close has been requested.
4. **L-3** (done in #21): when the read loop faults, close the transport (`RequestClose`) as well as completing
   `Completion`.

Then fix the lifecycle bugs (hangs, leaks and crashes):

5. **L-1** (done in #21): give each enumeration its own CTS linked to the connection, and cancel it on abort,
   completion or shutdown.
6. **L-2** (done in #21): never call `Cancel()` inline on the read loop or in Shutdown (use `CancelAsync` or catch
   everything), and make Shutdown always complete `Completion`.
7. **L-4, L-5** (L-5 done in #18, L-4 in #21): fail calls fast after shutdown, and don't dispose the write lock under waiters.
8. **D-1, D-2** (done in #20): exclude overrides of `object` members, compiler-generated and record members,
   `IDisposable`/`IAsyncDisposable`, and `async void` methods. Consider defaulting
   `IncludeInheritedMethods` to `false`.
9. **I-3** (done in #21): tag unknown methods as `_OTHER`, start the activity only after the method resolves, and
   log method-not-found below Error.
10. **R-3** (method-name truncation and `MaximumBatchSize` done in #21, id cap done since): stop echoing the method name (or truncate it), cap echoed string ids, and add a
    `MaximumBatchSize`. An outbound byte budget remains open.

Hardening and process:

11. Consider making the hardened limits the default, with `JsonRpcOptions.CreateTrusted()` (or
    similar) as the explicit opt-out. Reject negative option values (done). Apply `MaximumInboundMessageSize`
    to any `StreamMessageHandler` that has no cap of its own.
12. Add fuzzing (SharpFuzz) for the framing handlers, `RequestId`, and `HandleMessage`, which are
    the surfaces that parse peer bytes before authentication.
13. Add `SECURITY.md` and GitHub private vulnerability reporting (done in #17), enable `NuGetAudit` and CodeQL in
    CI, pin Actions to SHAs, and move publishing to NuGet trusted publishing (done in #16).
14. Add a test for every threat above, so each mitigation is pinned against regressions.
