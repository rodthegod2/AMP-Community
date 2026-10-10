using System;
using System.Collections.Generic;

namespace AMP.ModSync {
    // Which platforms a mod.io mod has a live file for (ModProfile.platforms: platform name + live modfile id, 0 = none)
    public struct ModioAvailability {
        public bool windows;
        public bool android;
        public bool oculus;

        public bool Quest { get { return android || oculus; } }

        public override string ToString() {
            return "windows=" + windows + " android=" + android + " oculus=" + oculus;
        }
    }

    public static class ModioRules {
        public static ModioAvailability Availability(IEnumerable<KeyValuePair<string, long>> platformFiles) {
            var result = new ModioAvailability();
            if(platformFiles == null) return result;
            foreach(KeyValuePair<string, long> p in platformFiles) {
                if(p.Value <= 0 || p.Key == null) continue;
                if(p.Key.Equals("windows", StringComparison.OrdinalIgnoreCase)) result.windows = true;
                else if(p.Key.Equals("android", StringComparison.OrdinalIgnoreCase)) result.android = true;
                else if(p.Key.Equals("oculus", StringComparison.OrdinalIgnoreCase)) result.oculus = true;
            }
            return result;
        }
    }
}
