using System;
using System.IO;
using System.Reflection;
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Nvim
{
    /// <summary>
    /// Loads the companion script that runs inside nvim.
    ///
    /// It lives in Lua/vsneo.lua rather than in a string literal here. As a literal
    /// every double quote had to be doubled, and a comment mentioning E325 once
    /// terminated the string and broke the build - a whole class of mistake that a
    /// real file simply does not have, on top of getting syntax highlighting and
    /// being readable on its own.
    ///
    /// It still ships inside the VSIX beside this assembly, so it cannot drift out
    /// of step with the rpcnotify contract the C# side expects. That is the reason
    /// it is not simply left to the user's init.lua: the extension does not work
    /// without it, so it is infrastructure rather than configuration. Loading the
    /// user's own config is a separate, opt-in thing.
    /// </summary>
    internal static class NvimLua
    {
        private const string FileName = "vsneo.lua";
        private static string _cached;

        /// <summary>
        /// The script, read once. Throws if it is missing: without the companion
        /// there is no mode and no cursor, so failing loudly here trips the breaker
        /// and leaves Visual Studio behaving normally rather than half-wired.
        /// </summary>
        public static string Script
        {
            get
            {
                if (_cached != null) return _cached;

                var path = Locate();
                _cached = File.ReadAllText(path);
                Log.Write("loaded " + FileName + " (" + _cached.Length + " chars) from " + path);
                return _cached;
            }
        }

        private static string Locate()
        {
            var beside = Path.GetDirectoryName(new Uri(typeof(NvimLua).Assembly.CodeBase).LocalPath);

            // Lua/ when deployed from the VSIX, alongside when copied flat.
            foreach (var candidate in new[]
            {
                Path.Combine(beside, "Lua", FileName),
                Path.Combine(beside, FileName),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                "VSNeo's companion script was not found next to the extension. " +
                "Looked in " + beside, FileName);
        }
    }
}
