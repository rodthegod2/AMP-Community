using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;

namespace AMP.Network.Nat {
    // A small UPnP IGD client: SSDP discovery, the device description, and the four SOAP actions AMP needs. Pure System.Net/System.Xml,
    // no Unity; call it from a background thread (every call blocks for up to a few seconds).

    public static class Ssdp {
        public static readonly IPEndPoint Multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        public static readonly string[] SearchTargets = {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        public static string BuildSearch(string searchTarget, int mxSeconds) {
            return "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: " + mxSeconds + "\r\nST: " + searchTarget + "\r\n\r\n";
        }

        // Headers of an "HTTP/1.1 200 OK" search response (names upper-cased); null for anything else
        public static Dictionary<string, string> ParseResponse(string text) {
            if(text == null) return null;
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string[] status = lines[0].Split(' ');
            if(status.Length < 2 || !status[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || status[1] != "200") return null;
            var headers = new Dictionary<string, string>();
            foreach(string line in lines.Skip(1)) {
                int colon = line.IndexOf(':');
                if(colon <= 0) continue;
                headers[line.Substring(0, colon).Trim().ToUpperInvariant()] = line.Substring(colon + 1).Trim();
            }
            return headers;
        }

        // The description URL from a response; only plain http on the address that answered (a device can't send us elsewhere)
        public static Uri Location(string response, IPAddress from) {
            Dictionary<string, string> headers = ParseResponse(response);
            string location;
            Uri uri;
            if(headers == null || !headers.TryGetValue("LOCATION", out location)) return null;
            if(!Uri.TryCreate(location, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
            IPAddress host;
            if(!IPAddress.TryParse(uri.Host, out host) || !host.Equals(from)) return null;
            return uri;
        }

        // Sends the searches (multicast on every IPv4 interface that has a gateway, or to one unicast test endpoint) and collects the
        // description URLs that answer within timeoutMs (a little longer than the first answer is not waited for).
        public static List<Uri> Discover(IPEndPoint target, int timeoutMs, Action<string> log) {
            var found = new List<Uri>();
            var sockets = new List<Socket>();
            try {
                bool multicast = target.Address.Equals(Multicast.Address);
                foreach(IPAddress local in multicast ? LocalAddress.WithGateway() : new List<IPAddress> { IPAddress.Any }) {
                    Socket s = null;
                    try {
                        s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        s.Bind(new IPEndPoint(local, 0));
                        if(multicast && !local.Equals(IPAddress.Any)) {
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                        }
                        for(int round = 0; round < 2; round++) { // UDP may drop one
                            foreach(string st in SearchTargets) {
                                byte[] data = Encoding.ASCII.GetBytes(BuildSearch(st, 2));
                                s.SendTo(data, target);
                            }
                        }
                        sockets.Add(s);
                    } catch(Exception e) {
                        log?.Invoke("SSDP: can't search from " + local + ": " + e.Message);
                        s?.Close();
                    }
                }

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var buffer = new byte[4096];
                while(DateTime.UtcNow < deadline && sockets.Count > 0) {
                    var readable = sockets.ToList();
                    Socket.Select(readable, null, null, 50000);
                    foreach(Socket s in readable) {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = s.ReceiveFrom(buffer, ref from); } catch(SocketException) { continue; }
                        Uri location = Location(Encoding.ASCII.GetString(buffer, 0, n), ((IPEndPoint) from).Address);
                        if(location == null || found.Contains(location)) continue;
                        found.Add(location);
                        // The router answered; give other devices a moment, but don't wait the whole timeout
                        DateTime soon = DateTime.UtcNow.AddMilliseconds(500);
                        if(soon < deadline) deadline = soon;
                    }
                }
            } finally {
                foreach(Socket s in sockets) s.Close();
            }
            return found;
        }
    }

    public class IgdService {
        public string ServiceType;
        public Uri ControlUrl;
    }

    public class IgdDescription {
        public string FriendlyName = "", Manufacturer = "", ModelName = "";
        public List<IgdService> Services = new List<IgdService>(); // WAN connection services, best first

        public string Name { get { return (Manufacturer + " " + (ModelName.Length > 0 ? ModelName : FriendlyName)).Trim(); } }

        private static readonly string[] Preferred = {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        // Control URLs must stay on the device that served the description
        public static IgdDescription Parse(string xml, Uri location) {
            XmlDocument doc = Xml.Load(xml);
            var result = new IgdDescription();
            Uri baseUrl = location;
            XmlElement urlBase = Xml.Elements(doc.DocumentElement, "URLBase").FirstOrDefault();
            Uri parsedBase;
            if(urlBase != null && Uri.TryCreate(urlBase.InnerText.Trim(), UriKind.Absolute, out parsedBase)) baseUrl = parsedBase;

            XmlElement device = Xml.Elements(doc.DocumentElement, "device").FirstOrDefault();
            if(device != null) {
                result.FriendlyName = Xml.Child(device, "friendlyName");
                result.Manufacturer = Xml.Child(device, "manufacturer");
                result.ModelName = Xml.Child(device, "modelName");
            }
            foreach(XmlElement service in Xml.Elements(doc.DocumentElement, "service")) {
                string type = Xml.Child(service, "serviceType"), control = Xml.Child(service, "controlURL");
                Uri url;
                if(Array.IndexOf(Preferred, type) < 0 || control.Length == 0 || !Uri.TryCreate(baseUrl, control, out url)) continue;
                if(url.Scheme != Uri.UriSchemeHttp || url.Host != location.Host) continue;
                result.Services.Add(new IgdService { ServiceType = type, ControlUrl = url });
            }
            result.Services = result.Services.OrderBy(s => Array.IndexOf(Preferred, s.ServiceType)).ToList();
            return result;
        }
    }

    // XML helpers that ignore namespaces (routers are not consistent about them) and refuse DTDs
    internal static class Xml {
        public static XmlDocument Load(string xml) {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null };
            using(var reader = XmlReader.Create(new StringReader(xml), settings)) doc.Load(reader);
            return doc;
        }

        public static IEnumerable<XmlElement> Elements(XmlNode root, string localName) {
            foreach(XmlNode node in root.SelectNodes("descendant-or-self::*[local-name()='" + localName + "']")) yield return (XmlElement) node;
        }

        public static string Child(XmlElement parent, string localName) {
            foreach(XmlNode node in parent.ChildNodes) {
                if(node is XmlElement && node.LocalName == localName) return node.InnerText.Trim();
            }
            return "";
        }
    }

    public static class Soap {
        public static string BuildRequest(string serviceType, string action, IList<KeyValuePair<string, string>> args) {
            var body = new StringBuilder();
            body.Append("<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
            body.Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");
            foreach(KeyValuePair<string, string> arg in args) body.Append('<').Append(arg.Key).Append('>').Append(SecurityElement.Escape(arg.Value ?? "")).Append("</").Append(arg.Key).Append('>');
            body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>\r\n");
            return body.ToString();
        }

        // The out arguments of "<action>Response"; null when the body has no such element
        public static Dictionary<string, string> ParseResponse(string xml, string action) {
            XmlElement response = Xml.Elements(Xml.Load(xml).DocumentElement, action + "Response").FirstOrDefault();
            if(response == null) return null;
            var values = new Dictionary<string, string>();
            foreach(XmlNode node in response.ChildNodes) if(node is XmlElement) values[node.LocalName] = node.InnerText.Trim();
            return values;
        }

        public static bool TryParseFault(string xml, out int code, out string description) {
            code = 0; description = "";
            try {
                XmlDocument doc = Xml.Load(xml);
                XmlElement error = Xml.Elements(doc.DocumentElement, "errorCode").FirstOrDefault();
                if(error == null || !int.TryParse(error.InnerText.Trim(), out code)) return false;
                XmlElement text = Xml.Elements(doc.DocumentElement, "errorDescription").FirstOrDefault();
                description = text == null ? "" : text.InnerText.Trim();
                return true;
            } catch(XmlException) {
                return false;
            }
        }
    }

    // A UPnP error from the router (Code = UPnP errorCode), or a transport problem (Code = -1)
    public class UpnpException : Exception {
        public readonly int Code;
        public UpnpException(int code, string message) : base(message) { Code = code; }

        public const int NoSuchEntry = 714, Conflict = 718, OnlyPermanentLeases = 725, NotAuthorized = 606;
    }

    public class PortMappingEntry {
        public int InternalPort;
        public string InternalClient = "", Description = "";
        public bool Enabled;
        public int LeaseSeconds;
    }

    public class IgdClient {
        public readonly IgdService Service;
        public readonly IgdDescription Device;
        public int TimeoutMs = 5000;
        private const int MaxBody = 256 * 1024;

        public IgdClient(IgdDescription device, IgdService service) { Device = device; Service = service; }

        public string GetExternalIPAddress() {
            Dictionary<string, string> r = Call("GetExternalIPAddress");
            string ip;
            return r.TryGetValue("NewExternalIPAddress", out ip) ? ip : "";
        }

        public void AddPortMapping(int port, string protocol, string internalClient, string description, int leaseSeconds) {
            Call("AddPortMapping", "NewRemoteHost", "", "NewExternalPort", port.ToString(), "NewProtocol", protocol, "NewInternalPort", port.ToString(),
                 "NewInternalClient", internalClient, "NewEnabled", "1", "NewPortMappingDescription", description, "NewLeaseDuration", leaseSeconds.ToString());
        }

        // null when the router has no mapping for this port and protocol
        public PortMappingEntry GetSpecificPortMappingEntry(int port, string protocol) {
            Dictionary<string, string> r;
            try {
                r = Call("GetSpecificPortMappingEntry", "NewRemoteHost", "", "NewExternalPort", port.ToString(), "NewProtocol", protocol);
            } catch(UpnpException e) when(e.Code == UpnpException.NoSuchEntry) {
                return null;
            }
            Func<string, string> get = key => { string v; return r.TryGetValue(key, out v) ? v : ""; };
            int internalPort, lease;
            int.TryParse(get("NewInternalPort"), out internalPort);
            int.TryParse(get("NewLeaseDuration"), out lease);
            return new PortMappingEntry { InternalPort = internalPort, InternalClient = get("NewInternalClient"), Description = get("NewPortMappingDescription"),
                                          Enabled = get("NewEnabled") == "1" || get("NewEnabled").Equals("true", StringComparison.OrdinalIgnoreCase), LeaseSeconds = lease };
        }

        public void DeletePortMapping(int port, string protocol) {
            Call("DeletePortMapping", "NewRemoteHost", "", "NewExternalPort", port.ToString(), "NewProtocol", protocol);
        }

        private Dictionary<string, string> Call(string action, params string[] nameValues) {
            var args = new List<KeyValuePair<string, string>>();
            for(int i = 0; i + 1 < nameValues.Length; i += 2) args.Add(new KeyValuePair<string, string>(nameValues[i], nameValues[i + 1]));
            byte[] body = Encoding.UTF8.GetBytes(Soap.BuildRequest(Service.ServiceType, action, args));

            var request = (HttpWebRequest) WebRequest.Create(Service.ControlUrl);
            request.Method = "POST";
            request.Proxy = null;
            request.KeepAlive = false;
            request.ServicePoint.Expect100Continue = false;
            request.Timeout = request.ReadWriteTimeout = TimeoutMs;
            request.ContentType = "text/xml; charset=\"utf-8\"";
            request.Headers.Add("SOAPAction", "\"" + Service.ServiceType + "#" + action + "\"");
            request.ContentLength = body.Length;
            string text;
            try {
                using(Stream s = request.GetRequestStream()) s.Write(body, 0, body.Length);
                using(var response = (HttpWebResponse) request.GetResponse()) text = Http.ReadBody(response, MaxBody);
            } catch(WebException e) {
                var response = e.Response as HttpWebResponse;
                if(response == null) throw new UpnpException(-1, action + ": " + e.Message);
                string fault;
                using(response) fault = Http.ReadBody(response, MaxBody);
                int code; string description;
                if(Soap.TryParseFault(fault, out code, out description)) throw new UpnpException(code, action + ": " + code + " " + description);
                throw new UpnpException(-1, action + ": HTTP " + (int) response.StatusCode);
            }
            Dictionary<string, string> values;
            try { values = Soap.ParseResponse(text, action); } catch(XmlException e) { throw new UpnpException(-1, action + ": bad answer (" + e.Message + ")"); }
            if(values == null) throw new UpnpException(-1, action + ": the router's answer has no " + action + "Response");
            return values;
        }
    }

    internal static class Http {
        public static string Get(Uri url, int timeoutMs) {
            var request = (HttpWebRequest) WebRequest.Create(url);
            request.Proxy = null;
            request.KeepAlive = false;
            request.Timeout = request.ReadWriteTimeout = timeoutMs;
            using(var response = (HttpWebResponse) request.GetResponse()) return ReadBody(response, 256 * 1024);
        }

        public static string ReadBody(HttpWebResponse response, int max) {
            using(Stream s = response.GetResponseStream())
            using(var copy = new MemoryStream()) {
                var buffer = new byte[8192];
                int n;
                while((n = s.Read(buffer, 0, buffer.Length)) > 0) {
                    copy.Write(buffer, 0, n);
                    if(copy.Length > max) throw new UpnpException(-1, "the router's answer is too large");
                }
                return Encoding.UTF8.GetString(copy.ToArray());
            }
        }
    }

    public static class LocalAddress {
        // The address this PC uses to reach target (a UDP "connect" sends nothing); null when there is no route
        public static IPAddress Towards(IPAddress target) {
            try {
                using(var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
                    s.Connect(new IPEndPoint(target, 9));
                    return ((IPEndPoint) s.LocalEndPoint).Address;
                }
            } catch(SocketException) {
                return null;
            }
        }

        // This PC's address in the home network: the one used for the internet, else the first one with a gateway
        public static IPAddress Lan() {
            IPAddress address = Towards(IPAddress.Parse("8.8.8.8"));
            if(address != null && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any)) return address;
            return WithGateway().FirstOrDefault(a => !a.Equals(IPAddress.Any));
        }

        // IPv4 addresses of interfaces that are up and have a gateway (skips virtual switches of VMs/WSL); all IPv4 ones if none has one
        public static List<IPAddress> WithGateway() {
            var withGateway = new List<IPAddress>();
            var all = new List<IPAddress>();
            try {
                foreach(NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
                    if(ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach(UnicastIPAddressInformation ua in props.UnicastAddresses) {
                        if(ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                        all.Add(ua.Address);
                        if(gateway) withGateway.Add(ua.Address);
                    }
                }
            } catch(Exception) { /* not supported on this platform: search on the default interface */ }
            if(withGateway.Count > 0) return withGateway;
            if(all.Count > 0) return all;
            return new List<IPAddress> { IPAddress.Any };
        }
    }
}
