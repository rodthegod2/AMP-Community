using System;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AMP.Network.Nat {
    // Join codes: an IPv4 address (and port) in a short, typo-checked form, e.g. "K7QX-29MB". Pure, no Unity.
    // Not a secret and not a relay id: anyone with the code knows the address. The format is fixed by its length:
    //   8 symbols  = address + CRC-8, for the default port 26950
    //   12 symbols = address + port + CRC-12
    // Alphabet: 32 symbols without 0, O, 1 and I; case-insensitive; dashes and spaces are ignored.
    public static class JoinCodes {
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        public const int DefaultPort = 26950;
        public const int ShortLength = 8, LongLength = 12;

        public static string Encode(IPAddress address, int port) {
            if(address == null || address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("join codes need an IPv4 address");
            if(port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
            byte[] ip = address.GetAddressBytes();
            ulong value;
            int symbols;
            if(port == DefaultPort) {
                value = (Payload(ip, 4) << 8) | Crc(ip, 8, 0x07);
                symbols = ShortLength;
            } else {
                byte[] data = { ip[0], ip[1], ip[2], ip[3], (byte) (port >> 8), (byte) port };
                value = (Payload(data, 6) << 12) | Crc(data, 12, 0x80F);
                symbols = LongLength;
            }
            var text = new StringBuilder();
            for(int i = symbols - 1; i >= 0; i--) {
                text.Append(Alphabet[(int) ((value >> (5 * i)) & 31)]);
                if(i > 0 && i % 4 == 0) text.Append('-');
            }
            return text.ToString();
        }

        // Strips dashes/spaces and upper-cases; null when the rest has characters outside the alphabet
        public static string Normalize(string text) {
            if(text == null) return null;
            var clean = new StringBuilder();
            foreach(char c in text) {
                if(c == '-' || char.IsWhiteSpace(c)) continue;
                char u = char.ToUpperInvariant(c);
                if(Alphabet.IndexOf(u) < 0) return null;
                clean.Append(u);
            }
            return clean.ToString();
        }

        public static bool TryDecode(string text, out IPAddress address, out int port, out string error) {
            address = null; port = 0; error = null;
            string code = Normalize(text);
            if(code == null) {
                error = HasLookalike(text) ? "Join codes never contain 0, O, 1 or I - check the code." : "That isn't a join code (letters and digits like K7QX-29MB).";
                return false;
            }
            if(code.Length != ShortLength && code.Length != LongLength) {
                error = "A join code has 8 or 12 letters and digits (this one has " + code.Length + ").";
                return false;
            }
            ulong value = 0;
            foreach(char c in code) value = (value << 5) | (uint) Alphabet.IndexOf(c);

            byte[] data;
            bool valid;
            if(code.Length == ShortLength) {
                data = Bytes(value >> 8, 4);
                valid = (value & 0xFF) == Crc(data, 8, 0x07);
                port = DefaultPort;
            } else {
                data = Bytes(value >> 12, 6);
                valid = (value & 0xFFF) == Crc(data, 12, 0x80F);
                port = (data[4] << 8) | data[5];
            }
            if(!valid || port == 0) { port = 0; error = "This code has a typo - check every letter."; return false; }
            address = new IPAddress(new[] { data[0], data[1], data[2], data[3] });
            return true;
        }

        private static bool HasLookalike(string text) {
            foreach(char c in text ?? "") if("0Oo1Ii".IndexOf(c) >= 0) return true;
            return false;
        }

        private static ulong Payload(byte[] data, int count) {
            ulong value = 0;
            for(int i = 0; i < count; i++) value = (value << 8) | data[i];
            return value;
        }

        private static byte[] Bytes(ulong value, int count) {
            var data = new byte[count];
            for(int i = count - 1; i >= 0; i--) { data[i] = (byte) value; value >>= 8; }
            return data;
        }

        // Bitwise MSB-first CRC of the given width (CRC-8: poly 0x07, CRC-12: poly 0x80F), init 0
        private static ulong Crc(byte[] data, int width, ulong poly) {
            ulong top = 1UL << (width - 1), mask = (1UL << width) - 1, crc = 0;
            foreach(byte b in data) {
                for(int bit = 7; bit >= 0; bit--) {
                    bool feedback = ((crc & top) != 0) ^ (((b >> bit) & 1) != 0);
                    crc = (crc << 1) & mask;
                    if(feedback) crc ^= poly;
                }
            }
            return crc;
        }
    }

    // What the player typed into the join field: a join code, an IPv4 address, a host name (with or without dots, e.g. "gamingpc" or a
    // Tailscale name), each optionally with ":port" (else the port field is used; a code brings its own port).
    // It is a code when, without dashes/spaces, it is 8 or 12 code-alphabet characters with a valid checksum. Code errors (typo, 0/O/1/I)
    // are only reported for input in the code's own shape (groups of 4 split by dashes or spaces); anything else is a host name.
    public static class JoinTarget {
        public const int MaxPort = 32767; // Netamite stores ports as short

        private static readonly Regex CodeShape = new Regex(@"^[A-Za-z0-9]{4}([- ]+[A-Za-z0-9]{4}){1,2}$");
        private static readonly Regex HostName = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$");

        public static bool TryParse(string text, string portText, out string host, out int port, out string error) {
            host = null; port = 0; error = null;
            text = text?.Trim() ?? "";
            if(text.Length == 0) { error = "Enter a join code or an IP address."; return false; }

            // Netamite connects over IPv4 only, and AMP hosts listen on IPv4
            if(text.StartsWith("[") || text.Count(c => c == ':') >= 2) { error = "IPv6 isn't supported - use the host's IPv4 address or a join code."; return false; }

            string candidate = text.TrimEnd('.');
            string compact = JoinCodes.Normalize(candidate);
            bool codeShaped = CodeShape.IsMatch(candidate);
            IPAddress address;
            if(compact != null && (compact.Length == JoinCodes.ShortLength || compact.Length == JoinCodes.LongLength)) {
                string codeError;
                if(JoinCodes.TryDecode(compact, out address, out port, out codeError)) {
                    if(port > MaxPort) { error = "This code is for port " + port + "; AMP ports go up to " + MaxPort + "."; port = 0; return false; }
                    host = address.ToString();
                    return true;
                }
                port = 0;
                if(codeShaped) { error = codeError; return false; }
            } else if(codeShaped || text.IndexOf(' ') >= 0) {
                string codeError;
                JoinCodes.TryDecode(candidate, out address, out port, out codeError);
                port = 0;
                error = codeError;
                return false;
            }

            int colon = text.LastIndexOf(':');
            if(colon >= 0) {
                portText = text.Substring(colon + 1);
                text = text.Substring(0, colon).Trim();
            }
            if(text.EndsWith(".")) text = text.Substring(0, text.Length - 1); // "host.example." is the same host
            if(!HostName.IsMatch(text)) { error = "\"" + text + "\" isn't a join code or an address."; return false; }
            if(!TryPort(portText, out port)) { error = "The port must be a number from 1 to " + MaxPort + "."; return false; }
            host = text;
            return true;
        }

        private static bool TryPort(string text, out int port) {
            return int.TryParse(text?.Trim(), out port) && port > 0 && port <= MaxPort;
        }
    }

    public enum AddressClass { Public, Shared, None }

    public static class NatRules {
        // Shared: private (10/8, 172.16/12, 192.168/16) or carrier-grade NAT (100.64/10): the router's "internet" address is not
        // reachable from the internet. None: no usable internet address (missing, 0/8, loopback 127/8, link-local 169.254/16,
        // benchmark 198.18/15, multicast/reserved 224+).
        public static AddressClass Classify(IPAddress address) {
            if(address == null || address.AddressFamily != AddressFamily.InterNetwork) return AddressClass.None;
            byte[] b = address.GetAddressBytes();
            if(b[0] == 0 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 198 && (b[1] == 18 || b[1] == 19))) return AddressClass.None;
            if(b[0] == 10) return AddressClass.Shared;
            if(b[0] == 172 && b[1] >= 16 && b[1] <= 31) return AddressClass.Shared;
            if(b[0] == 192 && b[1] == 168) return AddressClass.Shared;
            if(b[0] == 100 && b[1] >= 64 && b[1] <= 127) return AddressClass.Shared;
            return AddressClass.Public;
        }
    }
}
