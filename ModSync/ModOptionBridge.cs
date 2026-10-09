using System.Collections.Generic;
using System.Linq;
using ThunderRoad;

namespace AMP.ModSync {
    // The only place that touches ThunderRoad's ModOption objects. Main thread only.
    internal static class ModOptionBridge {
        internal static IEnumerable<ThunderRoad.ModManager.ModData> LoadedMods() {
            return ThunderRoad.ModManager.loadedMods.Where(m => m != null && m.modOptions != null);
        }

        // ThunderRoad collects the options before it loads their values and applies them (ApplyModOptions),
        // so "loaded" means every option of every mod has its values
        internal static bool OptionsLoaded() {
            return LoadedMods().Any(m => m.modOptions.Count > 0)
                && LoadedMods().All(m => m.modOptions.All(o => o == null || o.origParameterValues != null));
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

        // Index of the parameter holding this value (e.g. true, 20), or -1. A number between two steps
        // (25 for steps of 10) takes the nearest one.
        internal static int IndexOfValue(ModOption option, object value) {
            if(option.parameterValues == null) return -1;
            for(int i = 0; i < option.parameterValues.Length; i++) {
                ModOptionParameter parameter = option.parameterValues[i];
                if(parameter != null && value.Equals(parameter.value)) return i;
            }
            if(!(value is int)) return -1;

            int nearest = -1;
            for(int i = 0; i < option.parameterValues.Length; i++) {
                ModOptionParameter parameter = option.parameterValues[i];
                if(parameter == null || !(parameter.value is int)) continue;
                if(nearest < 0 || System.Math.Abs((int) parameter.value - (int) value) < System.Math.Abs((int) option.parameterValues[nearest].value - (int) value)) nearest = i;
            }
            return nearest;
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
            // A closed menu leaves a destroyed component behind: a Unity null check, not a plain one
            if((option.uiComponent as UnityEngine.Object) != null) option.RefreshUI();
        }
    }
}
