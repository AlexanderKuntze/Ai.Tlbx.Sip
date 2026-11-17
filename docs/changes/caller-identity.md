# Caller identity, dialled number and forwarding

Change proposal for `Ai.Tlbx.Sip`. It builds on [hold, resume, session refresh and media source switching](hold-session-refresh-media-source.md) and [blind and attended transfer](transfer-refer.md).

| | |
|---|---|
| Base | `31cbfa9` (11.12.0) plus the two changes above. |
| Scope | Core library `Ai.Tlbx.Sip` (new file `SipPartyIdentity.cs`, `SipCall.cs`), protocol tests, SipConsole sample. |
| Public API | Additive only. |
| Behaviour change | None for existing members. `RemoteUri` is unchanged. |
| Verification | Protocol tests 43/43 on net10.0 and net11.0 (Windows). Real-PBX observation at easybell Cloud PBX, see below. |

## Why

An application that answers calls needs to know who calls and which number was dialled: to look up the customer, to greet internal colleagues by name, and to tell the main number from a direct extension or a forwarded call. 11.11.0 exposes only `RemoteUri`, the raw URI of the `From` header for incoming calls.

The `From` header is set by the caller and can be spoofed. A PBX that has authenticated the caller asserts the identity in a separate header. Forwarding information is carried in yet other headers. Applications should not have to parse SIP headers for this.

## What changes

### 1. `SipPartyIdentity` (new)

`SipPartyIdentity.cs`

- `record SipPartyIdentity(string Uri, string? Number, string? DisplayName, bool Anonymous)`.
- Parses a name-addr or addr-spec (RFC 3261 section 20.10): quoted display names with escapes, `sip:`/`sips:` user parts and `tel:` numbers, unescaped and without URI parameters; header parameters after `>` (for example `;tag=`) are ignored.
- `Number` is null when the URI has no user part or the party is anonymous. Anonymous parties are recognised by the RFC 3323 conventions (display name `Anonymous`, host `anonymous.invalid`, user `anonymous`).
- Helpers split comma-separated header entries while respecting quotes and angle brackets, read header parameters, and order History-Info indices (`1`, `1.1`, `1.2`, `2`; RFC 7044 section 10.3).

### 2. Identities on `SipCall`

`SipCall.cs`

| Property | Incoming calls | Outgoing calls |
|---|---|---|
| `RemoteIdentity` | `P-Asserted-Identity` (RFC 3325), else `Remote-Party-ID`, else `From`. With several entries, the first one that carries a number wins (RFC 3325 allows one `sip:` and one `tel:` identity). | The dialled URI, replaced by `P-Asserted-Identity` or `To` of the 2xx answer. |
| `CalledIdentity` | The number originally dialled: the oldest `Diversion` entry (RFC 5806 lists the most recent first), else the History-Info entry with the lowest index, else `To`/Request-URI. **Null** if that would only be this endpoint's own registration user, because then the PBX did not tell the dialled number. | null |
| `Diversions` | All `Diversion` entries, most recent first. Empty if the PBX does not signal forwarding. | empty |
| `InitialHeaders` | All headers of the initial INVITE in received order, without `Authorization` and `Proxy-Authorization`, for PBX-specific information. | empty |

`Remote-Party-ID` is a widespread pre-standard header that PBXs still send instead of `P-Asserted-Identity`. Its `party`, `privacy` and `screen` parameters are not interpreted; the application can read them from `InitialHeaders`.

## Tests

| Test | Covers |
|---|---|
| Party identities: display names, tel URIs, anonymous callers, entry splitting and History-Info order | Escaped quotes and commas in display names, percent-encoded numbers, `tel:` URIs, RFC 3323 anonymity, URIs without user part, entry splitting, header parameters, index ordering |
| Incoming caller identity prefers P-Asserted-Identity and reports the originally dialled number | Spoofed `From` overridden by `P-Asserted-Identity` with `tel:` and `sip:` entries, two `Diversion` headers (original = oldest), History-Info ordering, own registration not reported as dialled number, `Remote-Party-ID` with `party=calling` and a `Diversion` as observed at a real PBX, authorization headers excluded |

`TestPbx.InviteAsync` accepts a `From` value and extra headers.

All 43 protocol/media tests pass on net10.0 and net11.0 on Windows. Linux was not run for this change.

## Real-PBX observation (easybell Cloud PBX)

Reported separately from the mock tests. Same test endpoint as before (dedicated extension, TLS 5061, SDES-SRTP); `SipConsole --show-identity` printed the parsed identities and all headers.

| Case | What the PBX sent | Result |
|---|---|---|
| External mobile call, direct | `Remote-Party-ID` with `party=calling;privacy=off;screen=no`, no display name; no `P-Asserted-Identity`; `To` = the endpoint's own registration user | caller number correct; `CalledIdentity` null |
| Internal desk phone | `Remote-Party-ID` with the device number and the extension's display name | number and display name correct |
| External call forwarded by a desk phone (unconditional) | `Diversion` with the forwarding device's number, display name and `reason=unconditional` | `Diversions` and `CalledIdentity` = forwarding device |

Observations relevant for applications:

- easybell does not tell the dialled number for direct calls. If an application needs to distinguish entry numbers, each entry has to reach the application in a distinguishable way (for example a separate extension or a forwarding that shows up as `Diversion`).
- `Diversion` carries the forwarding **device** number, which can differ from the extension number users know.
- For forwarded calls easybell replaced the caller's display name with the name of the forwarding extension. The number stays correct. Applications should not rely on the display name of forwarded calls.
- easybell also sends `Alert-Info` with `info=alert-external` or `info=alert-internal`; for the forwarded call it reported `alert-internal`. The library does not interpret it; it is available in `InitialHeaders`.

Not yet verified: forwarding through PBX groups or queues, `History-Info` from a real PBX, and anonymous callers.

## Sample

`samples/SipConsole`: `--show-identity` prints caller number, display name, anonymity, dialled number, forwarding chain and all initial headers of each incoming call.

## Known limitations

- `Privacy` and the `privacy`/`screen` parameters of `Remote-Party-ID` are exposed only as raw headers.
- Outgoing calls do not report a called identity beyond `RemoteIdentity`.
