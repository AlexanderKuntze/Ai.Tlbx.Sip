# Hold, resume, session refresh and media source switching

Change proposal for `Ai.Tlbx.Sip`.

| | |
|---|---|
| Base | `31cbfa9` (11.12.0). |
| Scope | Core library `Ai.Tlbx.Sip`, protocol tests, SipConsole sample. The VoiceAssistant adapter is unchanged. |
| Public API | Additive only. No existing member changed its signature. |
| Behaviour change | In-dialog `INVITE` and `UPDATE` are now accepted instead of being rejected with 488/405. |
| Verification | Protocol tests 39/39 on net10.0 and net11.0 (Windows). Real-PBX acceptance at easybell Cloud PBX, see below. |

## Why

A PBX uses in-dialog re-INVITE or UPDATE to hold, resume, park or re-route a call and to refresh a session. 11.11.0 answered these with `488 Not Acceptable Here` and `405 Method Not Allowed`. Depending on the PBX this can drop the call. Holding and transferring calls from the application side also needs a re-INVITE of its own.

A second problem showed up during the real-PBX test. When the remote party holds and resumes, some PBXs send no signalling to this leg at all (observed at easybell Cloud PBX). It only switches the RTP stream: hold music arrives under a new SSRC, and after resume the original stream continues. 11.11.0 locked onto the first remote SSRC and kept a single SRTP replay state, so all audio after resume was discarded. The caller could not be heard any more.

Both are prerequisites for an AI receptionist that holds the caller, consults a colleague and then transfers the call (REFER, a separate change that follows).

## What changes

### 1. SDP media direction (RFC 3264, RFC 4566)

`Protocol/SdpSession.cs`

- Parses `sendrecv`, `sendonly`, `recvonly` and `inactive` at session and media level; the media level wins (RFC 4566 section 6). Default is `sendrecv`.
- A connection address of `0.0.0.0` (or `::`) is treated as the legacy RFC 2543 hold (RFC 3264 section 8.4): the remote side never receives media.
- `AnswerDirection(offer, local)` computes the RFC 3264 section 6.1 answer: send only where the offer receives, receive only where the offer sends, both limited by the local wish.
- `Offer(...)` and `Answer(...)` take an optional direction and an SDP version. The `o=` session id stays constant for a call and the version increases with every new description (RFC 3264 section 8).
- Previously, any non-`sendrecv` attribute threw `NotSupportedException`.

### 2. Accepting re-INVITE and UPDATE (RFC 3261 section 14.2, RFC 3311)

`SipCall.cs` (`OnSessionRefreshAsync`, `BuildSessionRefreshResponse`, `AwaitRefreshAckAsync`)

- A re-INVITE or UPDATE with an SDP offer is answered with `200 OK` and an SDP answer in the complementary direction; the media session is updated at once.
- A re-INVITE without SDP is answered with an offer from the current direction, and the answer is taken from the ACK. An UPDATE without SDP (a pure session refresh) is answered with `200 OK` without a body.
- The `Contact` of the request refreshes the remote target.
- The `200 OK` to a re-INVITE is retransmitted until the ACK arrives (RFC 3261 section 13.3.1.4). Without an ACK within `TransactionTimeout` the call ends with BYE.
- A re-INVITE that overlaps an unfinished INVITE transaction gets `500` with `Retry-After` (RFC 3261 section 14.2). A request with `Require` gets `420 Bad Extension` and `Unsupported`. An unusable SDP gets `488`.
- Other in-dialog methods keep `405`, now with the reason phrase `Method Not Allowed`.

### 3. Local hold and resume (RFC 3264 section 8.4, RFC 6337 section 5.3)

`SipCall.cs` (`HoldAsync`, `ResumeAsync`, `RenegotiateAsync`)

- `HoldAsync` sends a re-INVITE offering `sendonly`. The remote party stops sending, while this side may still play hold music or announcements through `call.Audio`. `inactive` was not chosen because it would leave the held caller in silence.
- `ResumeAsync` offers `sendrecv`.
- Only one own re-INVITE runs at a time. Authentication challenges are handled by the existing `RequestAuthenticatedAsync`.
- `491 Request Pending` is retried up to twice after a random 2.1 to 4 seconds (RFC 3261 section 14.1).
- `408`, `481`, a transaction timeout or an unusable SDP answer end the dialog with BYE (RFC 3261 sections 12.2.1.2 and 14.1). Any other rejection leaves the session unchanged and throws `SipException` with the native status code.
- An incoming re-INVITE while an own re-INVITE is pending gets `491` (glare).
- A retransmitted `2xx` to an own re-INVITE is acknowledged again with the same ACK.

### 4. Applying a renegotiated media session

`SipCallAudio.cs` (`Update`, `Start`, send loop)

- `Update(session, direction)` applies a new remote address, RTCP endpoint, codec, SRTP key and direction while the call continues.
- A codec change resets codec, resamplers and the transmit queue. A changed remote SRTP key creates new receive contexts; the local key stays.
- Receive state (SSRC, jitter buffer, sequence tracking) restarts after every update.
- While the negotiated direction does not include sending (`recvonly`, `inactive`), no RTP is transmitted. Queued audio is consumed at real-time pace and the RTP timestamp keeps advancing; the RTP sequence number does not advance, because it counts transmitted packets only. RTCP continues.

### 5. Following a new remote media source (RFC 3550 section 8.2, RFC 3711 section 3.2.3)

`SipCallAudio.cs` (receive loop), `Media/SrtpContext.cs`

- The receiver switches to a new remote SSRC once the current SSRC has been silent for 200 ms (`SipCallAudio.SourceSwitchSilence`). Packets from another source while the current one is live are still ignored, so interleaved stray packets cannot take over the stream. A switch resets the jitter buffer and is reported as a diagnostic: `media: Remote media source changed (SSRC xxxxxxxx -> yyyyyyyy).`
- `SrtpContext` keeps rollover counter, highest index and replay window per SSRC, as RFC 3711 section 3.2.3 requires for crypto contexts. At most 8 receive streams are kept; the least recently used one is dropped. Only authenticated packets create or advance a stream's state. The send side is unchanged (one local SSRC).
- SRTCP receive state was not changed.

## Public API additions

| Member | Purpose |
|---|---|
| `enum SipMediaDirection { SendReceive, SendOnly, ReceiveOnly, Inactive }` | RFC 3264 direction from the point of view of the side that sent the SDP |
| `SipCall.HoldAsync(CancellationToken)` | Hold with a `sendonly` re-INVITE |
| `SipCall.ResumeAsync(CancellationToken)` | Resume with a `sendrecv` re-INVITE |
| `SipCall.IsOnHold` | True while this side holds the call |
| `SipCall.RemoteMediaDirection` | Direction the remote side last declared; `SendOnly`/`Inactive` means the remote side holds |
| `SipCall.MediaChanged` | Raised after a re-INVITE or UPDATE changed the session; handlers run outside the call lock |
| `SipCallAudio.Direction` | Negotiated direction from this endpoint's point of view |

Internal test hooks: `SipCall.GlareRetryMinimum`/`GlareRetryMaximum` and `SipCallAudio.SourceSwitchSilence`.

## Tests

`tests/Ai.Tlbx.Sip.Tests` (no external test framework, as before):

| Test | Covers |
|---|---|
| SDP media directions, legacy hold address and RFC 3264 answer directions | Parsing at session and media level, `0.0.0.0` hold, answer rules, origin version |
| Remote hold and resume by re-INVITE and UPDATE keep the call and pause transmission | `sendonly`, `sendrecv` and `inactive` offers, origin id kept and version increased, transmission paused while held, body-less UPDATE, `MediaChanged` sequence |
| SRTP keeps rollover and replay state per SSRC so a new PBX source decrypts | Second SSRC with a far lower sequence decrypts, replay still rejected per SSRC, original SSRC continues |
| Receiver follows a new remote SSRC after the old source stops | Interleaved packets ignored while the current source is live, switch after silence, diagnostic emitted |
| Local hold and resume by re-INVITE, rejected hold keeps the call, 491 glare is retried | `sendonly`/`sendrecv` offers and ACK, transmission continues while holding, 488 leaves the call unchanged, 491 retried |

`TestPbx` gained in-dialog requests from the PBX side (`SessionRefreshAsync`, sequence-aware `AckAsync`/`ByeAsync`), a restarted media stream (`SendAudioFromNewSourceAsync`) and answers to client re-INVITEs with the complementary direction, including configurable rejections (`RejectReinvites`).

All 39 protocol/media tests pass on net10.0 and net11.0 on Windows. Linux was not run for this change.

## Real-PBX acceptance (easybell Cloud PBX)

Reported separately from the mock tests, as the repository rules require. Test endpoint: a dedicated extension of the easybell Cloud PBX, TLS on port 5061 with SDES-SRTP, `SipConsole` with `--srtp --auto-answer --echo`, calls from a desk phone on the same PBX.

| Case | Result |
|---|---|
| Registration over TLS 5061 with SRTP | passed (also with unchanged 11.11.0) |
| Codec | G.722 negotiated |
| Remote hold and resume at the desk phone | easybell sends no re-INVITE to this leg; it switches to a hold-music SSRC and back. 11.11.0: no audio after resume. With this change: both switches followed, audio after resume. |
| Local hold with hold tone, then resume (`--hold-test`) | easybell answers the `sendonly` re-INVITE with `recvonly` in about 0.1 s; the hold tone is heard at the desk phone; resume answered `sendrecv` in about 0.08 s; echo audio returns |
| 35-minute call without session timer | no refresh from easybell, no drop (unchanged 11.11.0) |

Not yet verified at a real PBX: a re-INVITE without SDP, an UPDATE from the PBX, 491 glare and SRTP key changes.

## Sample

`samples/SipConsole`:

- prints `media renegotiated, remote=… local=…` on `MediaChanged`;
- `--hold-test`: five seconds after establishment the call is held, a 440 Hz tone is played for six seconds, then the call is resumed;
- echo mode does not echo while the call is held locally.

## Known limitations and follow-ups

- Transfers (REFER, blind and attended with Replaces) follow in [transfer-refer.md](transfer-refer.md).
- Session timers (RFC 4028), PRACK and DTMF remain unsupported.
- A codec change during a re-INVITE is applied, but only the initial offer order (G.722, PCMA, PCMU) is offered; the current codec is not preferred explicitly.
- `SipCall` raises `MediaChanged` for remote offers and own re-INVITEs alike; the argument is always the remote direction.
