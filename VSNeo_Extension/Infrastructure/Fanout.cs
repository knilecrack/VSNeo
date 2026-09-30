using System;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Multicast delivery that survives a throwing subscriber. A plain
    /// <c>handler?.Invoke(arg)</c> stops at the first exception, so with one
    /// mirror per open document listening, one bad handler starved every mirror
    /// subscribed after it of that event. Each subscriber gets its own try/catch
    /// and the throw is logged with the event's name.
    ///
    /// Allocates the invocation list per call, so it belongs on the per-edit
    /// events, not on the per-keystroke state push.
    /// </summary>
    internal static class Fanout
    {
        public static void Invoke<T>(Action<T>? handlers, T arg, string eventName)
        {
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)d)(arg);
                }
                catch (Exception ex)
                {
                    Log.Write("a " + eventName + " subscriber threw", ex);
                }
            }
        }
    }
}
