using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Infrastructure
{
    internal readonly struct VsCommandInfo
    {
        public readonly string Name;
        public readonly string DisplayName;
        public readonly string Bindings;

        public VsCommandInfo(string name, string displayName, string bindings)
        {
            Name = name;
            DisplayName = displayName;
            Bindings = bindings;
        }
    }

    internal static class VsCommandCatalog
    {
        public static VsCommandInfo[] Sort(IEnumerable<VsCommandInfo> commands)
        {
            var sorted = new List<VsCommandInfo>(commands);
            sorted.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return sorted.ToArray();
        }
    }
}
