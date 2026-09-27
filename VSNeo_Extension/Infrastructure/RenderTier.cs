using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VSNeo_Extension.Infrastructure
{
    /// <summary>
    /// Is WPF drawing this window on the CPU?
    ///
    /// On a normal machine every VSNeo effect is GPU work: WPF rasterizes
    /// through DirectX and a DropShadowEffect is a pixel shader. When WPF
    /// falls back to software rendering, the same effects become per-pixel
    /// CPU work on the UI thread - a blur recomputed every frame, particle
    /// overdraw, a full editor repaint per smooth-scroll frame - and over
    /// Remote Desktop each of those frames also crosses the network. That
    /// fallback happens:
    /// - with "Use hardware graphics acceleration if available" off in Visual
    ///   Studio (it sets RenderOptions.ProcessRenderMode to SoftwareOnly),
    /// - on a GPU-less VM or a driver WPF refuses (RenderCapability.Tier &lt; 2),
    /// - per window, when its HwndTarget is forced to software.
    ///
    /// Callers ask <see cref="ReduceEffects"/> at the start of an effect (a
    /// jump, a scroll, a blink restart), never per frame. vsneo_reduce_effects
    /// in ~/.vsneorc overrides the detection either way.
    /// </summary>
    internal static class RenderTier
    {
        // RenderCapability.Tier is process-wide and changes only on events
        // like a display driver switch; TierChanged resets the cache.
        private static int _tierIsSoftware = -1;   // -1 unknown, 0 no, 1 yes
        private static int _logged;

        static RenderTier()
        {
            RenderCapability.TierChanged += (s, e) => Volatile.Write(ref _tierIsSoftware, -1);
        }

        /// <summary>
        /// Whether the costly effects (particles, glow, smooth scrolling, the
        /// fading blink styles) should stand down for this visual's window.
        /// </summary>
        public static bool ReduceEffects(Visual? visual)
        {
            // -1 auto (detect), 0 never reduce, 1 always reduce.
            int preference = VSNeo_ExtensionPackage.Session?.State.ReduceEffects ?? -1;
            if (preference == 1) return true;
            if (preference == 0) return false;

            bool software = IsSoftware(visual);
            if (software && Interlocked.Exchange(ref _logged, 1) == 0)
                Log.Write("software rendering detected - particles, glow, smooth scrolling and "
                          + "fading blinks are off (vsneo_reduce_effects = false to force them on)");
            return software;
        }

        public static bool IsSoftware(Visual? visual)
        {
            try
            {
                if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly) return true;

                int tier = Volatile.Read(ref _tierIsSoftware);
                if (tier < 0)
                {
                    // The tier lives in the high word: 0 software, 1 partial,
                    // 2 full hardware acceleration.
                    tier = (RenderCapability.Tier >> 16) < 2 ? 1 : 0;
                    Volatile.Write(ref _tierIsSoftware, tier);
                }
                if (tier == 1) return true;

                if (visual != null
                    && PresentationSource.FromVisual(visual) is HwndSource source
                    && source.CompositionTarget != null
                    && source.CompositionTarget.RenderMode == RenderMode.SoftwareOnly)
                    return true;
            }
            catch
            {
                // A detection hiccup is not worth turning effects off for.
            }
            return false;
        }
    }
}
