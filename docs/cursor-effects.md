# Writing a cursor effect

Cursor effects are small classes in `VSNeo_Extension/Editor/Effects/`. The
cursor trail's frame loop drives them; you write what happens, not when frames
run.

## The pieces

| Type | What it is |
|---|---|
| `ICursorEffect` | The contract the host calls: four triggers, `Update(dt)`, `Render(dc, context)`, `Clear()`. |
| `CursorEffect` | Abstract base with every trigger a no-op. Subclass this for anything custom. |
| `ParticleEffect` | Base for particles: call `Emit(...)` from a trigger, draw one particle in `DrawParticle`. Storage, physics (velocity, acceleration, rotation) and lifetime are handled. |
| `HighlightEffect` | Base for timed one-shots at a cell: call `Start(cell, life)`, draw in `DrawHighlight` given `t` (0 → 1). |
| `CursorEffectContext` | What an effect may know: the cursor color (and a frozen brush), the user's `vsneo_cursor_vfx_*` settings, cell size, viewport, DPI, mode, a shared `Random`. |
| `CursorEffectRegistry` | Effect names → factories. The names are what users put in `vim.g.vsneo_cursor_vfx_mode`. |
| `CursorEffectHost` | The one WPF element all effects draw into. It isolates them: an effect that throws is turned off and logged. |

It is an abstract class plus an interface because Visual Studio runs on .NET
Framework 4.8, which has no default interface methods. Implement
`ICursorEffect` directly only if you need a different base.

## Triggers

| Trigger | When |
|---|---|
| `OnJump(context, from, to)` | The cursor moved to another cell (motions, jumps, clicks, typing). `from`/`to` are cell rects. |
| `OnType(context, cell)` | A character was typed (insert/replace mode, caret 1-2 columns further along the line). |
| `OnModeChanged(context, from, to, cell)` | The Vim mode changed. |
| `OnFocus(context, cell)` | The editor gained focus. |

All rects and points are **viewport-relative**: (0,0) is the top-left of what
is on screen.

## Example: a new particle effect

```csharp
using System.Windows;
using System.Windows.Media;

namespace VSNeo_Extension.Editor.Effects
{
    /// <summary>embers - on a jump: glowing embers float up from the destination.</summary>
    internal sealed class EmbersEffect : ParticleEffect
    {
        public override string Name => "embers";

        public override void OnJump(CursorEffectContext c, Rect from, Rect to)
        {
            var origin = Center(to);
            for (int i = 0; i < 8; i++)
            {
                var velocity = new Vector((c.Random.NextDouble() - 0.5) * 2, -1) * c.Speed * c.Unit;
                Emit(c.Random, origin, velocity, c.Lifetime * (0.5 + c.Random.NextDouble()));
            }
        }

        protected override void DrawParticle(DrawingContext dc, CursorEffectContext c, in Particle p, double life)
        {
            double r = c.CellWidth * 0.15;
            dc.DrawEllipse(c.Tint(c.Opacity * life), null, p.Position, r, r);
        }
    }
}
```

Then add one line to `CursorEffectRegistry.RegisterBuiltIns`:

```csharp
Register("embers", () => new EmbersEffect());
```

Users turn it on in `~/.vsneorc.lua`, and `:source` applies it live:

```lua
vim.g.vsneo_cursor_vfx_mode = { 'embers', 'glitch' }
```

## Rules

- **UI thread, inside the editor's frame.** Triggers and `Update` must be
  arithmetic and array writes: no I/O, no RPC, no waiting. A slow effect is a
  slow editor.
- **Say when you are done.** The frame loop runs only while some effect's
  `IsActive` is true. The bases handle this; a custom `CursorEffect` must
  return false once nothing is left, or the editor keeps repainting.
- **Fade with `context.Tint(alpha)`, never `PushOpacity`.** WPF renders every
  opacity push into an offscreen layer of its own; one per particle per frame
  is hundreds of surfaces a second and a visibly sluggish editor. `Tint`
  returns a cached, frozen translucent brush (null when fully transparent),
  and `context.Stroke(brush, thickness)` a cached pen over it. `Tint(color,
  alpha)` does the same for a fixed color (`GlitchEffect`'s red and cyan).
- **Don't allocate per particle in `Render`.** Tinted brushes and pens are
  cached; cache anything else you shape (see `MatrixEffect`'s glyph outlines).
- **Skip steps if you are big.** `OnJump` fires for every typed character and
  every `j`. An effect that flashes, sweeps or draws across the screen checks
  `IsStep(context, from, to)` (adjacent row, or two columns along its own)
  or `IsLineJump` and stays quiet for those; small particles need not.
- **Scale with the font.** Multiply speeds and sizes by `context.Unit`,
  `CellWidth` or `CellHeight`, so zoom does not change the look.
- **Respect the user's settings.** Use `context.Opacity`, `Lifetime`,
  `HighlightLifetime`, `Density` and `Speed` rather than constants where they
  make sense.
- **`IsCostly`** (default true) makes an effect stand down under software
  rendering (Remote Desktop, GPU-less VMs, VS hardware acceleration off).
  Override to false only for something genuinely tiny.
- Everything respects Windows' "Show animations" switch and
  `vsneo_reduce_effects` without any work on your side.
