using System.Collections.Generic;
using System.Linq;

namespace AMP.Network.Data {
    // maxItemsPerPlayer: which of a player's items the host removes when there are too many. Oldest first (lowest id), but never one that
    // is held or snapped: the cap used to take the starting weapon off a player's hip or out of their hand (audit 10). Game independent.
    public static class ItemCapPolicy {
        // owned: item id -> held/snapped. Fewer than (count - limit) ids come back when too many items are held.
        public static List<int> PickEvictions(IEnumerable<KeyValuePair<int, bool>> owned, int limit) {
            List<KeyValuePair<int, bool>> items = owned.ToList();
            if(limit <= 0 || items.Count <= limit) return new List<int>();
            return items.Where(i => !i.Value).OrderBy(i => i.Key).Take(items.Count - limit).Select(i => i.Key).ToList();
        }
    }
}
