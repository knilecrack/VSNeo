namespace VSNeo_Extension.Infrastructure
{
    /// <summary>How the relative line number margin decides whether to draw.</summary>
    public enum RelativeLineNumbersMode
    {
        /// <summary>Follow nvim's 'relativenumber' option; ':set rnu' toggles the margin live.</summary>
        FollowNeovim = 0,
        /// <summary>Always draw the margin; the cursor-line number still follows nvim's 'number'.</summary>
        AlwaysOn = 1,
        /// <summary>Do not draw the custom margin.</summary>
        AlwaysOff = 2,
        /// <summary>Always draw the margin with an absolute cursor-line number.</summary>
        Hybrid = 3,
    }

    internal static class RelativeLineNumberPolicy
    {
        internal static bool IsActive(RelativeLineNumbersMode mode, bool nvimRelativeNumber)
        {
            switch (mode)
            {
                case RelativeLineNumbersMode.AlwaysOn:
                case RelativeLineNumbersMode.Hybrid:
                    return true;
                case RelativeLineNumbersMode.AlwaysOff:
                    return false;
                default:
                    return nvimRelativeNumber;
            }
        }

        internal static bool IsCursorLineAbsolute(RelativeLineNumbersMode mode, bool nvimNumber) =>
            mode == RelativeLineNumbersMode.Hybrid || nvimNumber;
    }
}
