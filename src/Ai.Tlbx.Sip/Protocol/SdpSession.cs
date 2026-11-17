using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ai.Tlbx.Sip.Protocol;

internal sealed record SdpSession(IPAddress Address, int Port, int PayloadType, string Codec,
    int SampleRate, bool Secure, byte[]? MasterKey, int CryptoTag = 1, IPAddress? ControlAddress = null, int? ControlPort = null,
    SipMediaDirection Direction = SipMediaDirection.SendReceive)
{
    public IPAddress RtcpAddress => ControlAddress ?? Address;
    public int RtcpPort => ControlPort ?? Port + 1;
    /// <summary>True when the side that sent this SDP will transmit media.</summary>
    public bool Sends => Direction is SipMediaDirection.SendReceive or SipMediaDirection.SendOnly;
    /// <summary>True when the side that sent this SDP wants to receive media.</summary>
    public bool Receives => Direction is SipMediaDirection.SendReceive or SipMediaDirection.ReceiveOnly;
    public static SipMediaDirection Combine(bool send, bool receive) =>
        send ? receive ? SipMediaDirection.SendReceive : SipMediaDirection.SendOnly : receive ? SipMediaDirection.ReceiveOnly : SipMediaDirection.Inactive;
    /// <summary>RFC 3264 section 6.1: the answer may only send where the offer receives and receive where the offer sends.</summary>
    public static SipMediaDirection AnswerDirection(SdpSession offer, SipMediaDirection local) =>
        Combine(offer.Receives && local is SipMediaDirection.SendReceive or SipMediaDirection.SendOnly,
            offer.Sends && local is SipMediaDirection.SendReceive or SipMediaDirection.ReceiveOnly);
    internal static string Attribute(SipMediaDirection direction) => direction switch
    {
        SipMediaDirection.SendOnly => "sendonly", SipMediaDirection.ReceiveOnly => "recvonly",
        SipMediaDirection.Inactive => "inactive", _ => "sendrecv"
    };
    public static string Offer(IPAddress address, int port, byte[]? key, long sessionId, int? rtcpPort = null,
        SipMediaDirection direction = SipMediaDirection.SendReceive, long version = -1)
    {
        if (version < 0) version = sessionId;
        string family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IP6" : "IP4";
        var text = new StringBuilder($"v=0\r\no=- {sessionId} {version} IN {family} {address}\r\ns=Ai.Tlbx.Sip\r\nc=IN {family} {address}\r\nt=0 0\r\nm=audio {port} RTP/{(key is null ? "AVP" : "SAVP")} 9 8 0\r\na=rtpmap:9 G722/8000\r\na=rtpmap:8 PCMA/8000\r\na=rtpmap:0 PCMU/8000\r\na=ptime:20\r\na={Attribute(direction)}\r\n");
        if (key is not null) text.Append($"a=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:{Convert.ToBase64String(key)}\r\n");
        text.Append($"a=rtcp:{rtcpPort ?? port + 1} IN {family} {address}\r\n");
        return text.ToString();
    }
    public static string Answer(IPAddress address, int port, byte[]? key, long sessionId, SdpSession remote,
        SipMediaDirection local = SipMediaDirection.SendReceive, long version = -1, int? rtcpPort = null)
    {
        string offer = Offer(address, port, key, sessionId, rtcpPort, AnswerDirection(remote, local), version);
        offer = offer.Replace(" 9 8 0\r\n", $" {remote.PayloadType}\r\n");
        offer = offer.Replace("a=rtpmap:9 G722/8000\r\na=rtpmap:8 PCMA/8000\r\na=rtpmap:0 PCMU/8000\r\n", $"a=rtpmap:{remote.PayloadType} {remote.Codec}/8000\r\n");
        if (key is not null && remote.CryptoTag != 1) offer = offer.Replace("a=crypto:1 ", $"a=crypto:{remote.CryptoTag} ");
        return offer;
    }
    public static SdpSession Parse(byte[] bytes, SipMediaSecurity security)
    {
        if (bytes.Length > 16384) throw new InvalidDataException("SDP too large.");
        string? sessionAddress = null, mediaAddress = null, media = null;
        bool audio = false, foundAudio = false;
        var attributes = new List<string>(); var sessionAttributes = new List<string>();
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("m="))
            {
                audio = !foundAudio && line.StartsWith("m=audio ", StringComparison.Ordinal);
                if (audio) { media = line[2..]; foundAudio = true; }
            }
            else if (line.StartsWith("c="))
            {
                if (!foundAudio) sessionAddress = line[2..].Split(' ').Last();
                else if (audio) mediaAddress = line[2..].Split(' ').Last();
            }
            else if (audio && line.StartsWith("a=")) attributes.Add(line[2..]);
            else if (!foundAudio && line.StartsWith("a=")) sessionAttributes.Add(line[2..]);
        }
        if (media is null || !IPAddress.TryParse(mediaAddress ?? sessionAddress, out var address)) throw new InvalidDataException("SDP audio connection is missing or invalid.");
        var parts = media.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !int.TryParse(parts[1], out int port) || port is < 1 or > 65534)
            throw new InvalidDataException("Invalid SDP audio port.");
        bool secure = parts[2] == "RTP/SAVP";
        if (parts[2] is not ("RTP/AVP" or "RTP/SAVP")) throw new NotSupportedException("Unsupported SDP media transport.");
        if (secure != (security == SipMediaSecurity.SdesRequired)) throw new InvalidDataException("Media encryption does not match the required security policy.");
        // RFC 4566 section 6: a media-level direction overrides the session-level one; sendrecv is the default.
        static SipMediaDirection? DirectionOf(List<string> list) =>
            list.LastOrDefault(a => a is "sendrecv" or "sendonly" or "recvonly" or "inactive") switch
            {
                "sendonly" => SipMediaDirection.SendOnly, "recvonly" => SipMediaDirection.ReceiveOnly,
                "inactive" => SipMediaDirection.Inactive, "sendrecv" => SipMediaDirection.SendReceive, _ => null
            };
        var direction = DirectionOf(attributes) ?? DirectionOf(sessionAttributes) ?? SipMediaDirection.SendReceive;
        // RFC 3264 section 8.4: a zero connection address is the legacy RFC 2543 hold; never send media there.
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            direction = direction is SipMediaDirection.SendReceive or SipMediaDirection.SendOnly ? SipMediaDirection.SendOnly : SipMediaDirection.Inactive;
        if (attributes.Contains("rtcp-mux-only")) throw new NotSupportedException("RTCP multiplexing is not supported.");
        var control = attributes.Where(a => a.StartsWith("rtcp:", StringComparison.Ordinal)).ToArray();
        IPAddress? controlAddress = null; int? controlPort = null;
        if (control.Length > 1) throw new InvalidDataException("Ambiguous RTCP endpoint.");
        if (control.Length == 1)
        {
            var endpoint = control[0][5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (endpoint.Length is not (1 or 4) || !int.TryParse(endpoint[0], out int value) || value is < 1 or > 65535)
                throw new InvalidDataException("Invalid RTCP port.");
            controlPort = value;
            if (endpoint.Length == 4)
            {
                if (endpoint[1] != "IN" || endpoint[2] is not ("IP4" or "IP6") || !IPAddress.TryParse(endpoint[3], out controlAddress) ||
                    (endpoint[2] == "IP4") != (controlAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    throw new InvalidDataException("Invalid RTCP connection address.");
            }
        }
        int payload = -1; string codec = "";
        foreach (string p in parts.Skip(3))
        {
            if (!int.TryParse(p, out int id) || id is < 0 or > 127) continue;
            string mapping = attributes.FirstOrDefault(a => a.StartsWith($"rtpmap:{id} ", StringComparison.Ordinal))?.Split(' ', 2)[1] ?? (id == 9 ? "G722/8000" : id == 8 ? "PCMA/8000" : id == 0 ? "PCMU/8000" : "");
            if (mapping.Equals("G722/8000", StringComparison.OrdinalIgnoreCase) || mapping.Equals("G722/8000/1", StringComparison.OrdinalIgnoreCase)) { payload = id; codec = "G722"; break; }
            if (mapping.Equals("PCMA/8000", StringComparison.OrdinalIgnoreCase) || mapping.Equals("PCMA/8000/1", StringComparison.OrdinalIgnoreCase)) { payload = id; codec = "PCMA"; break; }
            if (mapping.Equals("PCMU/8000", StringComparison.OrdinalIgnoreCase) || mapping.Equals("PCMU/8000/1", StringComparison.OrdinalIgnoreCase)) { payload = id; codec = "PCMU"; break; }
        }
        if (payload < 0) throw new NotSupportedException("No supported audio codec was offered (PCMA/PCMU). ");
        byte[]? masterKey = null; int tag = 1;
        if (secure)
        {
            foreach (string a in attributes.Where(a => a.StartsWith("crypto:")))
            {
                var crypto = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (crypto.Length != 3 || crypto[1] != "AES_CM_128_HMAC_SHA1_80" || !crypto[2].StartsWith("inline:")) continue;
                // Lifetime and MKI require separate negotiation; never silently ignore them.
                if (crypto[2].Contains('|') || !int.TryParse(crypto[0][7..], out tag) || tag < 1) continue;
                masterKey = Convert.FromBase64String(crypto[2][7..]);
                if (masterKey.Length != 30) throw new InvalidDataException("Invalid SRTP master key length.");
                break;
            }
            if (masterKey is null) throw new NotSupportedException("No supported SRTP crypto attribute was offered.");
        }
        return new(address, port, payload, codec, codec == "G722" ? 16000 : 8000, secure, masterKey, tag, controlAddress, controlPort, direction);
    }
    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(30);
}
