using AMP.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AMP.Threading {
    public class Dispatcher {

		private static readonly ConcurrentQueue<Action> executionQueue = new ConcurrentQueue<Action>();

		// Max time per frame spent on queued actions, so a burst of packets cannot stall a VR frame
		private const long TICK_BUDGET_MS = 10;

		public static void UpdateTick() {
			Stopwatch stopwatch = Stopwatch.StartNew();
			Action action;
			lock(executionQueue) {
				while(stopwatch.ElapsedMilliseconds < TICK_BUDGET_MS && executionQueue.TryDequeue(out action)) {
					if (action != null) {
						try {
							action.Invoke();
						}catch(Exception ex) {
							Log.Err(ex);
						}
					}
				}
			}

            #if FULL_DEBUG
			Log.Debug($"Tick took {stopwatch.ElapsedMilliseconds}ms");
			#endif
        }

		public static void Enqueue(Action action) {
            #if FULL_DEBUG
			StackTrace stackTrace = new StackTrace();
            string caller = stackTrace.GetFrame(1).GetMethod().ReflectedType.Name + "." + stackTrace.GetFrame(1).GetMethod().Name;
			#endif

            executionQueue.Enqueue(() => {
                #if FULL_DEBUG
				Log.Warn("DEBUG", caller);
				#endif

				action();
			});
        }

	}
}
