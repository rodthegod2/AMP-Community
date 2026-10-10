# Easy Joining, Layer 1 (UPnP + Join Codes) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A Quest (or PC) friend joins a PC host over the internet without the host forwarding ports by hand. "Host on PC" opens the port on the router by itself (UPnP IGD) and shows a short join code; the joiner types the code. When that can't work (no UPnP router, the router refuses, the provider shares the IP / CGNAT) the host is told so in plain words. Steam hosting is unchanged.

**Facts checked:** Netamite's `IPServer` listens on ONE port for TCP and UDP (`Port` sets both; AMP passes `host_port`, default 26950, max 32767 because Netamite stores a `short`). So two mappings: TCP and UDP, same external/internal port. Hosting: `ModManager.HostServer` (IP) only; joining: every path goes through `GUIManager.JoinServer(address, port, password)` (book "Direct (IP)" page, H overlay, autopilot, mod-swap rejoin). The book's IP page already has its own on-screen keyboard (digits, letters, `-`) that works on Quest. The old `Compression/JoinCode.cs` (base64, unused) and the lobby "Use Code" (AMP master server codes for "Create >") stay as they are.

## Conventions + safety
As in every earlier phase (C# 7.3, net472, csproj entries, `build.sh`, ModSyncTests/packettest/policytest/AmpBot selftest, E2E via `tools/e2e/run-e2e.ps1` with all rules of `2026-10-10-e2e-harness.md`). Extra rules for this feature:
- AMP never changes Windows Firewall settings; it only tells the host that Windows may ask.
- E2E runs never touch the real router: in an isolated test run (`AMP_TEST_ISOLATED`) UPnP is OFF unless `-amp-test-ssdp <ip:port>` points it at a fake IGD.
- AMP never removes a mapping it didn't make: before adding, it reads the router's entry for the port; a mapping to this PC with another description (a manual port forward) is used as it is and left alone; a mapping to another device is reported, not replaced.
- Real-router test (owner approved): run once (a few times while debugging) with lease 600 s, only the AMP port, only this PC's LAN IP; verify, delete, verify gone. Report public vs CGNAT, never the full public IP.

## Design decisions
- **Own SSDP + SOAP client, not the Windows NATUPnP COM API.** COM interop from Unity's Mono is fragile, NATUPnP depends on the SSDP Discovery service, has no lease control, doesn't know WANIPConnection:2 and can't be pointed at a fake IGD for tests. The protocol subset needed is small (M-SEARCH, device description XML, four SOAP actions) and uses only `System.Net`/`System.Xml`, both already referenced. Runs on its own background thread; the main thread only reads a status snapshot.
- **Join code format (pure, versioned by length):** alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (32 symbols, no 0/O/1/I), case-insensitive, dashes/spaces ignored. Short code (8 symbols, `XXXX-XXXX`): IPv4 (32 bits) + CRC-8 for the default port 26950. Long code (12 symbols, `XXXX-XXXX-XXXX`): IPv4 + port (48 bits) + CRC-12. Pinned test vectors keep the format stable across versions. The code is the address in short form (not a secret, not a relay id).
- **Join field accepts code or address:** `GUIManager.JoinServer` resolves the address text through `JoinTarget.TryParse` (code, `IP`, `IP:port`, `host:port`, `localhost`), so every join path understands codes. The typed text is what's saved for next time.
- **External IP classes:** 10/8, 172.16/12, 192.168/16, 100.64/10 → "shared" (CGNAT or a router behind another router): no internet code, the UI says a relay is needed and same Wi-Fi still works. 0.0.0.0/empty → router not online.
- **Lease:** 7200 s, renewed every lease/2 while hosting; error 725 (OnlyPermanentLeasesSupported) or a refused lease → lease 0 once (no renewals; removed on stop). Stop/quit deletes only the mappings this session added; quitting waits up to 3 s for that.
- **Settings:** `hostingSettings.autoPortForward` (default true) for hosts who don't want AMP talking to the router.

## Tasks

### 1. Pure code: `Network/Nat/JoinCodes.cs`, `Network/Nat/Upnp.cs`, `Network/Nat/PortMapper.cs`
- `JoinCodes.Encode(IPAddress, int port)`, `TryDecode(text, out ip, out port, out error)`, `JoinTarget.TryParse(addressText, portText, out host, out port, out error)`, `NatRules.Classify(IPAddress)` (Public / Shared / None).
- `Ssdp` (build M-SEARCH, parse responses, discover on every IPv4 interface with a gateway or at a given unicast endpoint), `IgdDescription.Parse(xml, location)` (friendlyName, manufacturer, modelName, the first WANIPConnection:2/WANIPConnection:1/WANPPPConnection:1 with absolute control URL), `Soap` (build request, parse response/fault), `IgdClient` (GetExternalIPAddress, AddPortMapping, GetSpecificPortMappingEntry, DeletePortMapping over `HttpWebRequest`, no proxy, 5 s timeouts), `LocalAddress.Towards(IPAddress)` (UDP connect trick, no packet sent).
- `PortMapper` session (background thread): discover → external IP → classify → for TCP and UDP: read the existing entry, add with lease (fallback 0), remember what it added → renew → on Stop delete what it added. `PortMapStatus` snapshot + `Describe()` lines for the UI (pure).
- Tests (ModSyncTests, `NatTests`): code round-trips (edge IPs, ports, both lengths), pinned vectors, typo detection (every single-symbol substitution of sample codes), dashes/spaces/lowercase, 0/O/1/I message, target parsing (IP, IP:port, host:port, bad port > 32767), CGNAT/private ranges incl. borders, SSDP response parsing (case-insensitive headers), description parsing (IGD v1, v2 nested devices, URLBase, PPP), SOAP response/fault parsing, renewal timing, and the full flow against `FakeIgd` (SSDP responder + HTTP SOAP endpoint on 127.0.0.1, configurable: external IP, lease rejection 725, conflict 718, existing manual mapping, no GetSpecificPortMappingEntry): mapped, lease fallback, renew count with a short lease, CGNAT, conflict reported, manual forward left alone, delete on stop.

### 2. Game glue + UI
- `Network/Nat/HostPortMapping.cs`: `Start(port)` from `ModManager.HostServer`'s OnStart (IP hosting only), `Stop()` from `StopHost`, `WaitForCleanup(3000)` in `ModManager.Exit`. Test switch `-amp-test-ssdp` (isolated runs only); isolated runs without it: Off.
- Book connection page while hosting by IP: big "Internet code" (or "Same Wi-Fi code" when there's no internet code), lines with the Same Wi-Fi code, `IP:port`, router status in plain words, the firewall note. H overlay: the code. Join page: "Join Code / IP" button, field label "Code or IP:".
- Autopilot steps `expectPortMapping {state}` and `writeJoinCode {file, kind: lan|internet}`; `join` accepts a code as address (port optional).

### 3. Real-router test (once): `ModSyncTests.exe --real-router 26950 600`
Discover, router model + service type, external IP class, existing-entry check (abort if anything is mapped on that port), add TCP+UDP lease 600, verify with GetSpecificPortMappingEntry, delete, verify gone.

### 4. E2E
- AmpBot: `--join-code-file <file>` (waits for the file, decodes with `JoinCodes`, joins), `--fake-igd <ssdp port>` (runs `FakeIgd`; at the end requires TCP+UDP added for the port with description "AMP Community" and lease 7200, and both deleted after the game stopped hosting).
- Scenarios: `host-joincode` (UPnP Off in the test, bot joins with the LAN code), `host-upnp-fake` (game maps through the bot's fake IGD, bot joins with the Internet code, mappings deleted on quit), `join-code` (bot hosts, the game joins with a pinned long code for 127.0.0.1:26951).
- `-All` green.

### 5. Docs
`docs/HOW-TO-PLAY.txt`: hosting and joining by code first; port forwarding / Tailscale as the fallback; Quest section joins by code.

## Status (2026-10-10)
Tasks 1–5 done. Headless: 77 new NAT tests in ModSyncTests (523 in total, all pass), incl. the full flow against `FakeIgd` (open, lease fallback 725 → 0, renewals with a 2 s lease, CGNAT/private/no-internet, conflict with another device, a manual forward left alone, a left-over AMP mapping taken over, PPP without GetSpecificPortMappingEntry, no router, stop while searching, hosting again). Real router (owner-approved, run once with lease 600 s): SnapAV AN-110-RT-2L1W, WANIPConnection:2, public internet address; TCP+UDP 26950 added, verified with GetSpecificPortMappingEntry (this PC, "AMP Community", lease 600), deleted, verified gone. `run-e2e.ps1 -All`: 18 PASS (new: host-joincode, host-upnp-fake, join-code). Not verified: the book UI text on a headset (the page code runs only with the book open), joining over the real internet from outside the LAN, and a Quest typing a code.
