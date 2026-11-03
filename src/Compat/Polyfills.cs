// Small shims so the same sources compile for .NET 8 (AutoCAD 2025+) and
// .NET Framework 4.8 (AutoCAD 2021–2024).

#if NETFRAMEWORK
global using Infomatik.AutoCAD.Mcp.Json;
#else
global using System.Text.Json.Nodes;
#endif

using System;

namespace Infomatik.AutoCAD.Mcp
{
    internal static class Compat
    {
        public static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;

        /// <summary>Constant-time comparison (CryptographicOperations.FixedTimeEquals is .NET Core only).</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    // Required by the compiler for `record` / `init` on .NET Framework.
    internal static class IsExternalInit { }
}
#endif
