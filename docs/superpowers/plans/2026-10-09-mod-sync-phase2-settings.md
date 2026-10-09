# Mod Sync Phase 2 (Live Settings Sync) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While connected, every player runs the host's server-side mod settings (Mod Options of synced mods + AMP's host settings). Host changes show up live in everyone's menu; joiners can't change them; joiners get their own values back when they leave (also after a crash).

**Architecture:** Pure rules (which options are server-side, how to match a value across builds) live in `ModSync/SettingsRules.cs` and are unit-tested. Game glue: `ModOptionBridge` (read/apply ThunderRoad `ModOption`s and refresh their UI), `SettingsSyncHost` (snapshot on greet, broadcast on change), `SettingsSyncClient` (apply, lock by reverting, back up and restore). Messages reuse `ModSyncPacket` with two new kinds. AMP's host settings become AMP Mod Options in a "Host settings" category so they are synced by the same mechanism.

**Tech Stack:** C# 7.3 / net472 (Unity Mono), ThunderRoad ModOption API, Newtonsoft.Json, Netamite.

**Spec:** `docs/superpowers/specs/2026-10-09-mod-sync-design.md` component 10. Phase 1 plan for conventions: `docs/superpowers/plans/2026-10-09-mod-sync-phase1-handshake.md`.

## Conventions (same as phase 1)

- Repo `C:\Projects\bns-mp\amp`, branch `fix/review-pass`; push target `fork` → `community`.
- C# 7.3 only. New `.cs` files must be added to `Adammantium Multiplayer Mod.csproj` as `<Compile Include="..." />` next to the other `ModSync\` entries.
- Mod build: `bash /c/Projects/bns-mp/build.sh` (exit 0, no ` error `; never writes to the game folder).
- Tests: `cd /c/Projects/bns-mp/amp/Tests/ModSyncTests && dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/ModSyncTests.exe` (exit code = failures). packettest: `cd /c/Projects/bns-mp/packettest && dotnet build -nologo -v:q -nodeReuse:false -p:UseSharedCompilation=false && ./bin/Debug/net472/packettest.exe | tail -1` → `OK, 51 packets`. policytest: `cd /c/Projects/bns-mp/policytest && dotnet run -p:UseSharedCompilation=false` → 15 PASS.
- One build at a time; never `dotnet build-server shutdown`; never kill processes; never write to `C:\Program Files (x86)\Steam\...`.
- Commit: `git -c user.name=amp-review -c user.email=noreply@example.invalid commit`, trailer `Co-Authored-By: <your model> <noreply@anthropic.com>`. Preserve each file's line endings (use the Edit tool).
- ThunderRoad facts (verified by decompiling): `ThunderRoad.ModManager.loadedMods` is a `HashSet<ModManager.ModData>`; `ModData` has `folderName`, `modOptions` (`List<ModOption>`), `assemblies` (`List<Assembly>`). `ModOption` has `name`, `category`, `parameterValues` (`ModOptionParameter[]`, each with `title` and `value`), `GetIndex(false)` (current index), `Apply(int)` (sets value, saves to Options, fires `event Action<object> ValueChanged`), `uiComponent` (`IUserInterface`, null until the menu is built), `RefreshUI()`. `ThunderRoad.UIList.LoadValue(bool isDefaultValue)` re-reads the index; for sliders it re-triggers `Apply` and `ValueChanged`. `ModOptionIntValues(int start, int end, int step)` exists. AMP's own `ModManager` class name clashes with ThunderRoad's: always write `ThunderRoad.ModManager` for the game's.

## File structure

| File | Responsibility |
|---|---|
| `ModSync/SettingsRules.cs` (create) | `OptionValue`, `SettingsMessage` DTOs; `SettingsRules.IsServerSide`, `SettingsRules.MatchIndex`. Pure. |
| `ModSync/ModOptionBridge.cs` (create) | Enumerate/read/apply ThunderRoad ModOptions, refresh UI. Game. |
| `ModSync/SettingsSyncHost.cs` (create) | Host: list server-side options, snapshot to a greeted joiner, broadcast changes. Game. |
| `ModSync/SettingsSyncClient.cs` (create) | Joiner: apply host values, revert local changes, back up/restore, startup recovery + reconcile AMP host settings with config. Game. |
| `Network/Packets/Implementation/ModSyncPacket.cs` (modify) | Two new kinds, route to `SettingsSyncClient`. |
| `ModSync/ModSyncClient.cs` (modify) | Ignore the new kinds (they go to `SettingsSyncClient`). |
| `Data/SafeFile.cs` (modify) | `syncModSettings`, `settingOverrides`. |
| `ModLoader.cs` (modify) | AMP "Host settings" Mod Options. |
| `Network/Server/Server.cs` (modify) | Send settings snapshot at the end of `GreetPlayer`. |
| `ModManager.cs` (modify) | Start/stop host sync with hosting; restore on client stop; start the startup routine. |
| `Tests/ModSyncTests/*` (modify) | Tests for `SettingsRules`. |

---

### Task 1: SettingsRules (pure, TDD)

**Files:** Create `ModSync/SettingsRules.cs`; modify `Tests/ModSyncTests/ModSyncTests.csproj`, `Tests/ModSyncTests/Program.cs`, `Adammantium Multiplayer Mod.csproj`.

- [ ] **Step 1: Failing tests.** Add `<Compile Include="..\..\ModSync\SettingsRules.cs" />` to the test csproj. In `Program.cs` call `SettingsTests();` in `Main` after the existing test groups and add:

```csharp
    private static void SettingsTests() {
        var personal  = new[] { "MyTool" };
        var overrides = new System.Collections.Generic.Dictionary<string, string> { { "SpellMod/Spell Volume", "server" }, { "SpellMod/Damage", "personal" } };

        T("gameplay option of a normal mod is server-side", SettingsRules.IsServerSide("SpellMod", "Fire Damage", "Combat", false, personal, overrides));
        T("volume option is personal", !SettingsRules.IsServerSide("SpellMod", "Music Volume", "", false, personal, overrides));
        T("category words count too", !SettingsRules.IsServerSide("SpellMod", "Size", "HUD", false, personal, overrides));
        T("'ui' only matches as a whole word", SettingsRules.IsServerSide("SpellMod", "Build Speed", "Guide", false, personal, overrides));
        T("haptics (longer word) is personal", !SettingsRules.IsServerSide("SpellMod", "Haptics Strength", "", false, personal, overrides));
        T("options of personal mods are never synced", !SettingsRules.IsServerSide("mytool", "Fire Damage", "", false, personal, overrides));
        T("override 'server' wins over the word list", SettingsRules.IsServerSide("SpellMod", "Spell Volume", "", false, personal, overrides));
        T("override 'personal' wins", !SettingsRules.IsServerSide("SpellMod", "Damage", "", false, personal, overrides));
        T("AMP: only the Host settings category", SettingsRules.IsServerSide("MultiplayerMod", "PvP", SettingsRules.AmpHostCategory, true, personal, overrides)
                                                 && !SettingsRules.IsServerSide("MultiplayerMod", "Player Nametags", "Gameplay", true, personal, overrides));
        T("null lists are fine", SettingsRules.IsServerSide("SpellMod", "Fire Damage", null, false, null, null));

        var titles = new[] { "Off", "Low", "High" };
        T("index and title agree", SettingsRules.MatchIndex(titles, new OptionValue { index = 2, title = "High" }) == 2);
        T("title wins when the index moved", SettingsRules.MatchIndex(titles, new OptionValue { index = 0, title = "Low" }) == 1);
        T("unknown title is no match", SettingsRules.MatchIndex(titles, new OptionValue { index = 1, title = "Ultra" }) == -1);
        T("out of range index with unknown title is no match", SettingsRules.MatchIndex(titles, new OptionValue { index = 9, title = "?" }) == -1);
        T("no titles is no match", SettingsRules.MatchIndex(new string[0], new OptionValue { index = 0, title = "Off" }) == -1);
    }
```

- [ ] **Step 2: Run tests** → build fails (`SettingsRules`, `OptionValue` missing).

- [ ] **Step 3: Implement `ModSync/SettingsRules.cs`:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AMP.ModSync {
    // One Mod Options value as sent host -> joiner. The title is compared too, because indexes can differ between builds of a mod.
    public class OptionValue {
        public string mod;
        public string option;
        public string title;
        public int    index;
    }

    public class SettingsMessage {
        public List<OptionValue> values = new List<OptionValue>();
    }

    public static class SettingsRules {
        // AMP's own options in this category are the host's server settings; the rest of AMP's options are personal
        public const string AmpHostCategory = "Host settings";

        // Words that mark an option as a personal preference (sound, menus, controls, comfort) rather than gameplay
        private static readonly string[] PersonalWords = {
            "volume", "sound", "audio", "music", "voice", "mic", "microphone", "ui", "menu", "hud", "display",
            "graphic", "graphics", "haptic", "haptics", "vibration", "control", "controls", "key", "keybind",
            "pose", "subtitle", "subtitles", "language", "nametag", "nametags", "healthbar", "healthbars"
        };

        public static bool IsServerSide(string modFolder, string optionName, string category, bool isAmp,
                                        ICollection<string> personalMods, IDictionary<string, string> overrides) {
            if(personalMods != null && personalMods.Any(p => string.Equals(p, modFolder, StringComparison.OrdinalIgnoreCase))) return false;

            string chosen;
            if(overrides != null && overrides.TryGetValue(modFolder + "/" + optionName, out chosen)) {
                if(string.Equals(chosen, "server", StringComparison.OrdinalIgnoreCase))   return true;
                if(string.Equals(chosen, "personal", StringComparison.OrdinalIgnoreCase)) return false;
            }

            if(isAmp) return category == AmpHostCategory;

            foreach(string word in Words(optionName).Concat(Words(category))) {
                foreach(string personal in PersonalWords) {
                    // Short words must match exactly ("ui" must not hit "guide"), longer ones may be a prefix ("haptics")
                    if(word == personal || (personal.Length >= 4 && word.StartsWith(personal))) return false;
                }
            }
            return true;
        }

        // Index of the host's value in our own option, or -1 if we can't be sure it's the same value
        public static int MatchIndex(string[] titles, OptionValue value) {
            if(titles == null || titles.Length == 0 || value == null) return -1;
            if(value.index >= 0 && value.index < titles.Length && titles[value.index] == value.title) return value.index;
            return Array.IndexOf(titles, value.title);
        }

        private static IEnumerable<string> Words(string text) {
            if(string.IsNullOrEmpty(text)) return Enumerable.Empty<string>();
            return new string(text.Select(c => char.IsLetter(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
                   .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
```

- [ ] **Step 4: Run tests** → all PASS.
- [ ] **Step 5:** Add `<Compile Include="ModSync\SettingsRules.cs" />` to the mod csproj, build (exit 0), commit `Settings sync: rules for server-side options and value matching`.

---

### Task 2: ModOptionBridge, settings fields, AMP host-setting options

**Files:** Create `ModSync/ModOptionBridge.cs`; modify `Data/SafeFile.cs`, `ModLoader.cs`, csproj.

- [ ] **Step 1: Settings fields.** In `Data/SafeFile.cs` `HostingSettings`, after the `personalMods` line add:

```csharp
            // Joiners use the host's gameplay Mod Options while connected (see ModSync/SettingsRules.cs)
            public bool     syncModSettings        = true;
            // Force an option to be synced or not: { "<mod folder>/<option name>": "server" | "personal" }
            public Dictionary<string, string> settingOverrides = new Dictionary<string, string>();
```

Add `using System.Collections.Generic;` at the top of the file if missing.

- [ ] **Step 2: Create `ModSync/ModOptionBridge.cs`:**

```csharp
using System.Collections.Generic;
using System.Linq;
using ThunderRoad;

namespace AMP.ModSync {
    // The only place that touches ThunderRoad's ModOption objects. Main thread only.
    internal static class ModOptionBridge {
        internal static IEnumerable<ThunderRoad.ModManager.ModData> LoadedMods() {
            return ThunderRoad.ModManager.loadedMods.Where(m => m != null && m.modOptions != null);
        }

        internal static bool OptionsLoaded() {
            return LoadedMods().Any(m => m.modOptions.Count > 0);
        }

        internal static bool IsAmp(ThunderRoad.ModManager.ModData mod) {
            return mod.assemblies != null && mod.assemblies.Any(a => a != null && a.GetName().Name == "AMP");
        }

        internal static ModOption Find(string modFolder, string optionName) {
            ThunderRoad.ModManager.ModData mod = LoadedMods().FirstOrDefault(m => m.folderName == modFolder);
            return mod == null ? null : mod.modOptions.FirstOrDefault(o => o != null && o.name == optionName);
        }

        internal static string[] Titles(ModOption option) {
            if(option.parameterValues == null) return new string[0];
            return option.parameterValues.Select(p => p == null ? "" : (p.title ?? (p.value == null ? "" : p.value.ToString()))).ToArray();
        }

        internal static OptionValue Read(ModOption option) {
            int index = option.GetIndex(false);
            string[] titles = Titles(option);
            return new OptionValue {
                mod    = option.modData.folderName,
                option = option.name,
                index  = index,
                title  = index >= 0 && index < titles.Length ? titles[index] : "",
            };
        }

        // Sets the value like the menu would and makes an open menu show it
        internal static void Apply(ModOption option, int index) {
            option.Apply(index);
            UIList list = option.uiComponent as UIList;
            if(list != null) list.LoadValue(false);
            option.RefreshUI();
        }
    }
}
```

Add `<Compile Include="ModSync\ModOptionBridge.cs" />` to the csproj.

- [ ] **Step 3: AMP host settings as Mod Options.** In `ModLoader.cs`, after the last existing `[ModOption...]` member of the "Voice Chat" category, add:

```csharp
        // Server settings for games you host. Joiners see the host's values here, live and locked (settings sync).
        [ModOptionCategory(ModSync.SettingsRules.AmpHostCategory, 20)]
        [ModOptionOrder(30)]
        [ModOptionTooltip("Players can hurt each other on servers you host.")]
        [ModOption("PvP", saveValue = true)]
        public static void SetPvp(bool enabled = true) {
            ChangeHostSetting(s => s.pvpEnable = enabled);
        }

        [ModOptionCategory(ModSync.SettingsRules.AmpHostCategory, 20)]
        [ModOptionOrder(31)]
        [ModOptionTooltip("How much damage players do to each other, in percent.")]
        [ModOption("PvP Damage %", saveValue = true)]
        [ModOptionIntValues(0, 200, 10)]
        public static void SetPvpDamage(int percent = 20) {
            ChangeHostSetting(s => s.pvpDamageMultiplier = percent / 100f);
        }

        [ModOptionCategory(ModSync.SettingsRules.AmpHostCategory, 20)]
        [ModOptionOrder(32)]
        [ModOptionTooltip("Other players may load a different map.")]
        [ModOption("Players Change Map", saveValue = true)]
        public static void SetAllowMapChange(bool allowed = true) {
            ChangeHostSetting(s => s.allowMapChange = allowed);
        }

        [ModOptionCategory(ModSync.SettingsRules.AmpHostCategory, 20)]
        [ModOptionOrder(33)]
        [ModOptionTooltip("Only the host runs enemy AI. Steadier fights, more load on the host.")]
        [ModOption("Host Runs Enemies", saveValue = true)]
        public static void SetHostAuthoritativeNpcs(bool enabled = false) {
            ChangeHostSetting(s => s.hostAuthoritativeNpcs = enabled);
        }

        // Mod Options can be applied before the config is loaded; SettingsSyncClient reconciles both once it is
        internal static bool hostSettingsReconciled = false;

        private static void ChangeHostSetting(Action<SafeFile.HostingSettings> change) {
            if(ModManager.safeFile == null) return;
            change(ModManager.safeFile.hostingSettings);
            // Values pushed by a host we joined must not end up in our own config file
            if(hostSettingsReconciled && !ModSync.SettingsSyncClient.ApplyingHostValues) ModManager.safeFile.Save();
        }
```

Add `using System;` / `using AMP.Data;` to `ModLoader.cs` if missing. (`ModManager` here is AMP's `ModManager`, as elsewhere in that file.)

- [ ] **Step 4: Build.** It fails until Task 3 adds `SettingsSyncClient.ApplyingHostValues`. To keep this task buildable, create `ModSync/SettingsSyncClient.cs` as a stub now:

```csharp
namespace AMP.ModSync {
    internal static class SettingsSyncClient {
        internal static bool ApplyingHostValues { get { return false; } }
    }
}
```

Add it to the csproj. Build (exit 0). Commit `Settings sync: option bridge, host settings as Mod Options`.

---

### Task 3: SettingsSyncHost + SettingsSyncClient + wiring

**Files:** Create `ModSync/SettingsSyncHost.cs`; replace `ModSync/SettingsSyncClient.cs`; modify `ModSyncPacket.cs`, `ModSyncClient.cs`, `Server.cs`, `ModManager.cs`, csproj.

- [ ] **Step 1: Packet kinds.** In `Network/Packets/Implementation/ModSyncPacket.cs` add after `JOINER_REPORT`:

```csharp
        public const byte SETTINGS_SNAPSHOT = 3; // host -> joiner, all server-side values, sent when the joiner is greeted
        public const byte SETTING_CHANGED   = 4; // host -> joiners, one value the host just changed
```

and make `ProcessClient` route them:

```csharp
        public override bool ProcessClient(NetamiteClient client) {
            if(kind == SETTINGS_SNAPSHOT || kind == SETTING_CHANGED) SettingsSyncClient.OnPacket(this);
            else ModSyncClient.OnPacket(this);
            return true;
        }
```

(`ModSyncClient.OnPacket` already returns early for kinds other than `HOST_MOD_SET`; leave that check in place.)

- [ ] **Step 2: Create `ModSync/SettingsSyncHost.cs`:**

```csharp
using AMP.Data;
using AMP.Logging;
using AMP.Network.Data;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Netamite.Server.Data;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using ThunderRoad;

namespace AMP.ModSync {
    // Host side of settings sync: sends the host's server-side Mod Options to greeted joiners and broadcasts changes.
    internal static class SettingsSyncHost {
        private static readonly List<KeyValuePair<ModOption, Action<object>>> subscriptions = new List<KeyValuePair<ModOption, Action<object>>>();

        private static bool Enabled { get { return ModManager.safeFile.hostingSettings.syncModSettings; } }

        // Main thread, when hosting starts
        internal static void Start() {
            Stop();
            if(!Enabled) return;
            foreach(ModOption option in ServerSideOptions()) {
                ModOption captured = option;
                Action<object> handler = _ => OnChanged(captured);
                option.ValueChanged += handler;
                subscriptions.Add(new KeyValuePair<ModOption, Action<object>>(option, handler));
            }
            Log.Info(Defines.SERVER, $"Settings sync: {subscriptions.Count} server-side options.");
        }

        // Main thread, when hosting stops
        internal static void Stop() {
            foreach(var subscription in subscriptions) subscription.Key.ValueChanged -= subscription.Value;
            subscriptions.Clear();
        }

        // Any thread: called at the end of Server.GreetPlayer
        internal static void SendSnapshot(ClientData client) {
            if(!Enabled) return;
            Dispatcher.Enqueue(() => {
                var message = new SettingsMessage { values = ServerSideOptions().Select(ModOptionBridge.Read).ToList() };
                Send(new[] { client }, ModSyncPacket.SETTINGS_SNAPSHOT, message);
            });
        }

        private static void OnChanged(ModOption option) {
            if(ModManager.serverInstance == null) return;
            var message = new SettingsMessage();
            message.values.Add(ModOptionBridge.Read(option));
            ClientInformation[] playing = ModManager.serverInstance.netamiteServer.Clients
                                            .Where(c => ModSyncHost.IsPlaying((ClientData) c)).ToArray();
            Send(playing, ModSyncPacket.SETTING_CHANGED, message);
        }

        private static void Send(IEnumerable<ClientInformation> clients, byte kind, SettingsMessage message) {
            if(ModManager.serverInstance == null || message.values.Count == 0) return;
            List<ModSyncPacket> packets = ModSyncPacket.Create(kind, JsonConvert.SerializeObject(message));
            foreach(ClientInformation client in clients) {
                foreach(ModSyncPacket packet in packets) ModManager.serverInstance.netamiteServer.SendTo(client, packet);
            }
        }

        private static IEnumerable<ModOption> ServerSideOptions() {
            var settings = ModManager.safeFile.hostingSettings;
            foreach(ThunderRoad.ModManager.ModData mod in ModOptionBridge.LoadedMods()) {
                bool isAmp = ModOptionBridge.IsAmp(mod);
                foreach(ModOption option in mod.modOptions) {
                    if(option == null) continue;
                    if(SettingsRules.IsServerSide(mod.folderName, option.name, option.category, isAmp, settings.personalMods, settings.settingOverrides)) yield return option;
                }
            }
        }
    }
}
```

Check before writing: `ModSyncHost.IsPlaying(ClientData)` exists (phase 1); `netamiteServer.SendTo(ClientInformation, NetPacket)` exists (used in ModSyncHost). If `netamiteServer.Clients` returns `ClientInformation[]` use it as above.

- [ ] **Step 3: Replace `ModSync/SettingsSyncClient.cs`:**

```csharp
using AMP.Data;
using AMP.GameInteraction.Components;
using AMP.Logging;
using AMP.Network.Packets.Implementation;
using AMP.Threading;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThunderRoad;
using UnityEngine;

namespace AMP.ModSync {
    // Joiner side of settings sync. Main thread only, except OnPacket.
    internal static class SettingsSyncClient {
        private const string BackupFile = "settings-backup.json";

        private static readonly MessageAssembler assembler = new MessageAssembler();
        private static readonly Dictionary<string, int>            hostIndex = new Dictionary<string, int>();
        private static readonly Dictionary<string, OptionValue>    originals = new Dictionary<string, OptionValue>();
        private static readonly Dictionary<string, ModOption>      locked    = new Dictionary<string, ModOption>();
        private static readonly Dictionary<string, Action<object>> handlers  = new Dictionary<string, Action<object>>();
        private static float lastNotice = -10f;

        // True while AMP itself changes options, so the change isn't reverted or saved to our config
        internal static bool ApplyingHostValues { get; private set; }

        internal static void OnPacket(ModSyncPacket packet) {
            string json = assembler.Add("settings:" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;
            SettingsMessage message;
            try {
                message = JsonConvert.DeserializeObject<SettingsMessage>(json);
            } catch(JsonException) {
                return;
            }
            if(message == null || message.values == null) return;
            Dispatcher.Enqueue(() => Apply(message));
        }

        private static void Apply(SettingsMessage message) {
            if(ModManager.serverInstance != null) return; // we are the host: our own values are the host's
            if(ModManager.clientInstance == null) return;

            foreach(OptionValue value in message.values) {
                if(value == null) continue;
                ModOption option = ModOptionBridge.Find(value.mod, value.option);
                if(option == null) continue; // we don't have that mod (or that option)
                int index = SettingsRules.MatchIndex(ModOptionBridge.Titles(option), value);
                if(index < 0) continue;

                string key = value.mod + "/" + value.option;
                if(!originals.ContainsKey(key)) {
                    originals[key] = ModOptionBridge.Read(option);
                    SaveBackup();
                }
                hostIndex[key] = index;
                if(!handlers.ContainsKey(key)) {
                    Action<object> handler = _ => OnLocalChange(key);
                    option.ValueChanged += handler;
                    handlers[key] = handler;
                    locked[key]   = option;
                }
                if(option.GetIndex(false) != index) Set(option, index);
            }
        }

        // The player changed a host-controlled option: put the host's value back next frame
        private static void OnLocalChange(string key) {
            if(ApplyingHostValues) return;
            Dispatcher.Enqueue(() => {
                int index;
                ModOption option;
                if(!hostIndex.TryGetValue(key, out index) || !locked.TryGetValue(key, out option)) return;
                if(option.GetIndex(false) == index) return;
                Set(option, index);
                if(Time.time - lastNotice > 2f) {
                    lastNotice = Time.time;
                    TextDisplay.ShowTextDisplay(new DisplayTextPacket("settingsync", "This setting is set by the host.", Color.yellow, Vector3.forward * 2, true, true, 3));
                }
            });
        }

        private static void Set(ModOption option, int index) {
            ApplyingHostValues = true;
            try {
                ModOptionBridge.Apply(option, index);
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, $"Settings sync: couldn't set {option.name}: {e.Message}");
            } finally {
                ApplyingHostValues = false;
            }
        }

        // Main thread: when we leave a server. Puts our own values back.
        internal static void Restore() {
            foreach(var handler in handlers) {
                ModOption option;
                if(locked.TryGetValue(handler.Key, out option)) option.ValueChanged -= handler.Value;
            }
            handlers.Clear();
            locked.Clear();
            hostIndex.Clear();
            assembler.Forget("");

            RestoreValues(originals.Values.ToList());
            originals.Clear();
            DeleteBackup();
        }

        private static void RestoreValues(List<OptionValue> values) {
            foreach(OptionValue value in values) {
                ModOption option = ModOptionBridge.Find(value.mod, value.option);
                if(option == null) continue;
                int index = SettingsRules.MatchIndex(ModOptionBridge.Titles(option), value);
                if(index >= 0 && option.GetIndex(false) != index) Set(option, index);
            }
        }

        // Coroutine started once at game start: waits until Mod Options exist, then
        // 1) brings AMP's host-setting options and config.json in line, 2) restores values left over from a crash.
        internal static IEnumerator StartupRoutine() {
            float waited = 0f;
            while(!ModOptionBridge.OptionsLoaded() && waited < 120f) {
                yield return new WaitForSeconds(1f);
                waited += 1f;
            }
            ReconcileHostSettings();

            List<OptionValue> leftover = LoadBackup();
            if(leftover.Count > 0) {
                Log.Info(Defines.CLIENT, $"Settings sync: restoring {leftover.Count} settings from the last session.");
                RestoreValues(leftover);
            }
            DeleteBackup();
        }

        // Options saved in the menu win; options never touched take the value from config.json
        private static void ReconcileHostSettings() {
            ThunderRoad.ModManager.ModData amp = ModOptionBridge.LoadedMods().FirstOrDefault(ModOptionBridge.IsAmp);
            if(amp != null && ModManager.safeFile != null) {
                var settings = ModManager.safeFile.hostingSettings;
                foreach(ModOption option in amp.modOptions.Where(o => o != null && o.category == SettingsRules.AmpHostCategory)) {
                    int saved;
                    if(GameManager.options.TryGetModOptionParameter(amp.folderName, option.name, out saved, option.saveValue)) {
                        Set(option, saved); // pushes the saved menu value into hostingSettings (no config save while reconciling)
                        continue;
                    }
                    string title = ConfigTitle(option.name, settings);
                    int index = title == null ? -1 : Array.IndexOf(ModOptionBridge.Titles(option), title);
                    if(index >= 0) Set(option, index);
                }
            }
            ModLoader.hostSettingsReconciled = true;
        }

        // Title of the option value that matches the current config.json value
        private static string ConfigTitle(string optionName, SafeFile.HostingSettings settings) {
            switch(optionName) {
                case "PvP":                 return settings.pvpEnable.ToString();
                case "PvP Damage %":        return Mathf.RoundToInt(settings.pvpDamageMultiplier * 100f).ToString();
                case "Players Change Map":  return settings.allowMapChange.ToString();
                case "Host Runs Enemies":   return settings.hostAuthoritativeNpcs.ToString();
                default:                    return null;
            }
        }

        private static string BackupPath { get { return Path.Combine(ModSyncPaths.CacheFolder ?? "", BackupFile); } }

        private static void SaveBackup() {
            if(!ModSyncPaths.Available) return;
            try {
                Directory.CreateDirectory(ModSyncPaths.CacheFolder);
                File.WriteAllText(BackupPath, JsonConvert.SerializeObject(originals.Values.ToList()));
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }

        private static List<OptionValue> LoadBackup() {
            if(!ModSyncPaths.Available || !File.Exists(BackupPath)) return new List<OptionValue>();
            try {
                return JsonConvert.DeserializeObject<List<OptionValue>>(File.ReadAllText(BackupPath)) ?? new List<OptionValue>();
            } catch(JsonException) {
                return new List<OptionValue>();
            } catch(IOException) {
                return new List<OptionValue>();
            }
        }

        private static void DeleteBackup() {
            if(!ModSyncPaths.Available) return;
            try { File.Delete(BackupPath); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
    }
}
```

Before writing, verify against the real code and adjust if a name differs (report any change):
- `ThunderRoad.GameManager.options.TryGetModOptionParameter(string modName, string parameterName, out int index, bool saveValue)` signature (decompile `ThunderRoad.Options` if unsure: `ilspycmd -t ThunderRoad.Options "<Managed>\ThunderRoad.dll" > C:\Projects\bns-mp\decomp\Options.cs`).
- Bool option titles: check how ThunderRoad builds titles for bool options (`ModOption.GetDefaults` in `C:\Projects\bns-mp\decomp\ThunderRoad_full\ThunderRoad\ModOption.cs` ~line 400). If bool titles aren't `"True"`/`"False"`, make `ConfigTitle` produce the right titles; same for int values (`"20"`).
- `ModLoader` is in namespace `AMP` and `hostSettingsReconciled` is accessible (internal static).

- [ ] **Step 4: Wiring.**
  1. `Network/Server/Server.cs`, end of `GreetPlayer(ClientData client, bool loadedLevel = false)`, after `client.LoadedLevel = true;` add `ModSync.SettingsSyncHost.SendSnapshot(client);`.
  2. `ModManager.cs`, `HostServer(...)` and `HostSteamServer(...)`: after `ModSync.ModSyncHost.Prepare();` add `ModSync.SettingsSyncHost.Start();`. In `StopHost()` add `ModSync.SettingsSyncHost.Stop();` before `serverInstance.Stop();`.
  3. `ModManager.cs`, `StopClientImmediatly()`: add `ModSync.SettingsSyncClient.Restore();` before `clientInstance?.Disconnect();`.
  4. `ModManager.cs`, right after the `try { ModSync.ModSyncPaths.Init(); } catch ...` block: `StartCoroutine(ModSync.SettingsSyncClient.StartupRoutine());` (ModManager is a MonoBehaviour; if this method is static or runs before the GameObject is active, use `ModManager.instance.StartCoroutine(...)` instead and report it).
  5. csproj: `<Compile Include="ModSync\SettingsSyncHost.cs" />`.

- [ ] **Step 5: Build** (exit 0), run all suites (ModSyncTests ALL PASSED, packettest `OK, 51 packets`, policytest 15 PASS). Commit `Settings sync: host snapshot and live changes, joiner applies, locks and restores`.

---

### Task 4: Verify, review, push

- [ ] Full build + all suites + nettest (`cd /c/Projects/bns-mp/nettest && ./bin/Debug/net472/nettest.exe` → `ALL PASSED`).
- [ ] Final review (superpowers:code-reviewer), fix confirmed issues.
- [ ] Push `git push fork fix/review-pass:community`, refresh `C:\Projects\bns-mp\test-build\MultiplayerMod`, add a "Settings sync" note to `HOW-TO-PLAY.txt` (AMP Mod Options → Host settings; joiners' gameplay options follow the host; `syncModSettings`, `settingOverrides`).

In-game checks for the user:
1. Host changes a spell mod's damage option while a friend is connected → friend's menu shows the new value within a second.
2. Friend tries to change it → it snaps back with "This setting is set by the host."
3. Friend's music volume isn't touched.
4. Friend leaves → their own values are back. Friend's game crashes while connected → values are back after restarting.
5. Host toggles AMP → Host settings → PvP off mid-game → PvP damage stops; friend's AMP menu shows PvP off.
