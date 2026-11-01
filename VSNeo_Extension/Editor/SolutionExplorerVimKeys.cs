namespace VSNeo_Extension.Editor
{
    internal enum SolutionExplorerVimKey
    {
        None,
        J,
        K,
        G,
        ShiftG,
        H,
        L,
        O,
        Enter,
        Q,
    }

    internal enum SolutionExplorerVimAction
    {
        None,
        Wait,
        Up,
        Down,
        Top,
        Bottom,
        Collapse,
        Expand,
        Open,
        ReturnToEditor,
    }

    internal static class SolutionExplorerVimKeys
    {
        internal const int TimeoutMs = 1000;

        internal static SolutionExplorerVimAction Next(ref int pendingGUntil,
            SolutionExplorerVimKey key, int now)
        {
            bool pending = pendingGUntil != 0 && unchecked(now - pendingGUntil) < 0;
            pendingGUntil = 0;
            if (pending && key == SolutionExplorerVimKey.G) return SolutionExplorerVimAction.Top;

            switch (key)
            {
                case SolutionExplorerVimKey.G:
                    pendingGUntil = unchecked(now + TimeoutMs);
                    return SolutionExplorerVimAction.Wait;
                case SolutionExplorerVimKey.J: return SolutionExplorerVimAction.Down;
                case SolutionExplorerVimKey.K: return SolutionExplorerVimAction.Up;
                case SolutionExplorerVimKey.ShiftG: return SolutionExplorerVimAction.Bottom;
                case SolutionExplorerVimKey.H: return SolutionExplorerVimAction.Collapse;
                case SolutionExplorerVimKey.L: return SolutionExplorerVimAction.Expand;
                case SolutionExplorerVimKey.O:
                case SolutionExplorerVimKey.Enter: return SolutionExplorerVimAction.Open;
                case SolutionExplorerVimKey.Q: return SolutionExplorerVimAction.ReturnToEditor;
                default: return SolutionExplorerVimAction.None;
            }
        }
    }
}
