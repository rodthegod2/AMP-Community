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
using ClientType = AMP.Network.Client.Client;

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
            ClientType client = ModManager.clientInstance;
            if(client == null) return;
            string json = assembler.Add("settings:" + packet.transferId, packet.part, packet.parts, packet.data);
            if(json == null) return;
            SettingsMessage message;
            try {
                message = JsonConvert.DeserializeObject<SettingsMessage>(json);
            } catch(JsonException) {
                return;
            }
            if(message == null || message.values == null) return;
            // Values of a server we already left must not reach the next one
            Dispatcher.Enqueue(() => {
                if(ModManager.clientInstance == client) Apply(message);
            });
        }

        private static void Apply(SettingsMessage message) {
            if(ModManager.serverInstance != null) return; // we are the host: our own values are the host's
            if(ModManager.clientInstance == null) return;

            // A backup from a crash that wasn't restored yet holds the player's real values, our current ones are the old host's
            Dictionary<string, OptionValue> unrestored = null;
            bool addedOriginal = false;

            foreach(OptionValue value in message.values) {
                if(value == null) continue;
                ModOption option = ModOptionBridge.Find(value.mod, value.option);
                if(option == null) continue; // we don't have that mod (or that option)
                int index = SettingsRules.MatchIndex(ModOptionBridge.Titles(option), value);
                if(index < 0) continue;

                string key = value.mod + "/" + value.option;
                if(!originals.ContainsKey(key)) {
                    if(unrestored == null) {
                        unrestored = new Dictionary<string, OptionValue>();
                        foreach(OptionValue old in LoadBackup()) unrestored[old.mod + "/" + old.option] = old;
                    }
                    OptionValue previous;
                    originals[key] = unrestored.TryGetValue(key, out previous) ? previous : ModOptionBridge.Read(option);
                    addedOriginal = true;
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
            if(addedOriginal) SaveBackup();
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

            // Entries of an earlier crash that are still waiting (see StartupRoutine) are restored too
            List<OptionValue> values = originals.Values.ToList();
            values.AddRange(LoadBackup().Where(v => !originals.ContainsKey(v.mod + "/" + v.option)));
            originals.Clear();
            if(values.Count > 0) RestoreAndClean(values);
        }

        // Sets the values, writes the Options file (the game only does that on its own schedule, a quit would lose it)
        // and only then drops the backup. Entries whose option exists but has no values yet stay in the backup.
        private static void RestoreAndClean(List<OptionValue> values) {
            List<OptionValue> notLoaded = RestoreValues(values);
            if(SaveOptionsNow()) WriteBackup(notLoaded);
        }

        // Returns the entries whose option exists but isn't loaded yet; options we don't have at all are dropped
        private static List<OptionValue> RestoreValues(List<OptionValue> values) {
            var notLoaded = new List<OptionValue>();
            foreach(OptionValue value in values) {
                ModOption option = ModOptionBridge.Find(value.mod, value.option);
                if(option == null) continue;
                string[] titles = ModOptionBridge.Titles(option);
                if(titles.Length == 0) {
                    notLoaded.Add(value);
                    continue;
                }
                int index = SettingsRules.MatchIndex(titles, value);
                if(index >= 0 && option.GetIndex(false) != index) Set(option, index);
            }
            return notLoaded;
        }

        // Same call the game's own menu makes when it closes
        private static bool SaveOptionsNow() {
            try {
                if(ThunderRoad.Options.isDirty) ThunderRoad.Options.SaveCoroutine().AsSynchronous();
                return true;
            } catch(Exception e) {
                Log.Err(Defines.CLIENT, "Settings sync: couldn't save the options. " + e.Message);
                return false;
            }
        }

        // Coroutine started once at game start: waits until Mod Options are loaded and applied, then
        // 1) brings AMP's host-setting options and config.json in line, 2) restores values left over from a crash.
        internal static IEnumerator StartupRoutine() {
            // Must be taken now: ThunderRoad applies every option later (a default for the ones never saved),
            // which overwrites hostingSettings from config.json before we get to look at it
            HashSet<string> hadSavedValue = SavedHostOptions();
            Dictionary<string, object> configValues = ConfigValues();

            // A mod whose options failed to load keeps this from ever being true: go on after a while with what is there
            float waited = 0f;
            while(!ModOptionBridge.OptionsLoaded() && waited < 600f) {
                yield return new WaitForSecondsRealtime(1f);
                waited += 1f;
            }
            if(waited >= 600f) Log.Warn(Defines.CLIENT, "Settings sync: Mod Options did not finish loading, continuing with the ones that did.");
            ReconcileHostSettings(hadSavedValue, configValues);

            List<OptionValue> leftover = LoadBackup();
            if(leftover.Count > 0) {
                Log.Info(Defines.CLIENT, $"Settings sync: restoring {leftover.Count} settings from the last session.");
                RestoreAndClean(leftover);
            }
        }

        // Names of AMP's host-setting options the player already has a saved menu value for. Null: can't tell.
        private static HashSet<string> SavedHostOptions() {
            ThunderRoad.ModManager.ModData amp = ModOptionBridge.LoadedMods().FirstOrDefault(ModOptionBridge.IsAmp);
            if(amp == null || GameManager.options == null) return null;
            var saved = new HashSet<string>();
            foreach(ModOption option in amp.modOptions.Where(o => o != null && o.category == SettingsRules.AmpHostCategory)) {
                int index;
                if(GameManager.options.TryGetModOptionParameter(amp.folderName, option.name, out index, option.saveValue)) saved.Add(option.name);
            }
            return saved;
        }

        private static Dictionary<string, object> ConfigValues() {
            var values = new Dictionary<string, object>();
            if(ModManager.safeFile == null) return values;
            SafeFile.HostingSettings settings = ModManager.safeFile.hostingSettings;
            values["PvP"]                = settings.pvpEnable;
            values["PvP Damage %"]       = Mathf.RoundToInt(settings.pvpDamageMultiplier * 100f);
            values["Players Change Map"] = settings.allowMapChange;
            values["Host Runs Enemies"]  = settings.hostAuthoritativeNpcs;
            return values;
        }

        // Options saved in the menu win (ThunderRoad already applied them to the config in memory);
        // options never saved take the value from config.json
        private static void ReconcileHostSettings(HashSet<string> hadSavedValue, Dictionary<string, object> configValues) {
            ThunderRoad.ModManager.ModData amp = ModOptionBridge.LoadedMods().FirstOrDefault(ModOptionBridge.IsAmp);
            if(amp != null && hadSavedValue != null) {
                foreach(ModOption option in amp.modOptions.Where(o => o != null && o.category == SettingsRules.AmpHostCategory)) {
                    object configValue;
                    if(hadSavedValue.Contains(option.name) || !configValues.TryGetValue(option.name, out configValue)) continue;
                    int index = ModOptionBridge.IndexOfValue(option, configValue);
                    if(index >= 0) Set(option, index); // no config save while reconciling, hostSettingsReconciled is still false
                }
            }
            ModLoader.hostSettingsReconciled = true;
        }

        // Each save profile has its own backup: values saved under one profile are never restored into the other
        private static string BackupPath {
            get { return Path.Combine(ModSyncPaths.CacheFolder ?? "", ProfileSwitch.Active ? "settings-backup.AMP_Multiplayer.json" : BackupFile); }
        }

        // Adds this session's originals to what an earlier, not yet restored session left in the file
        private static void SaveBackup() {
            List<OptionValue> all = LoadBackup().Where(v => !originals.ContainsKey(v.mod + "/" + v.option)).ToList();
            all.AddRange(originals.Values);
            WriteBackup(all);
        }

        private static void WriteBackup(List<OptionValue> values) {
            if(!ModSyncPaths.Available) return;
            if(values.Count == 0) {
                DeleteBackup();
                return;
            }
            try {
                Directory.CreateDirectory(ModSyncPaths.CacheFolder);
                File.WriteAllText(BackupPath, JsonConvert.SerializeObject(values));
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
