// RcBackport — net472 / C# 7.3 compatibility shims for the vendored DotRecast subset.
//
// This file is NOT part of DotRecast. It exists only so the vendored sources (see
// ThirdParty/DotRecast/NOTICE.md) compile against .NET Framework 4.7.2 with LangVersion 7.3.
// The .NET Framework BCL lacks System.MathF, Math.Clamp, Array.Fill and float.IsFinite.
//
// BACKPORTED for net472/C# 7.3 — see ThirdParty/DotRecast/NOTICE.md

using System;

namespace System
{
    /// <summary>
    /// Minimal System.MathF shim for .NET Framework 4.7.2 (which has no MathF).
    /// Only the members used by the vendored DotRecast subset are provided.
    /// </summary>
    internal static class MathF
    {
        public const float PI = (float)Math.PI;
        public const float E = (float)Math.E;

        public static float Abs(float x) { return Math.Abs(x); }
        public static float Ceiling(float x) { return (float)Math.Ceiling(x); }
        public static float Floor(float x) { return (float)Math.Floor(x); }
        public static float Round(float x) { return (float)Math.Round(x); }
        public static float Sqrt(float x) { return (float)Math.Sqrt(x); }
        public static float Cos(float x) { return (float)Math.Cos(x); }
        public static float Sin(float x) { return (float)Math.Sin(x); }
        public static float Tan(float x) { return (float)Math.Tan(x); }
        public static float Acos(float x) { return (float)Math.Acos(x); }
        public static float Asin(float x) { return (float)Math.Asin(x); }
        public static float Atan(float x) { return (float)Math.Atan(x); }
        public static float Atan2(float y, float x) { return (float)Math.Atan2(y, x); }
        public static float Pow(float x, float y) { return (float)Math.Pow(x, y); }
        public static float Exp(float x) { return (float)Math.Exp(x); }
        public static float Log(float x) { return (float)Math.Log(x); }
        public static float Log(float x, float b) { return (float)Math.Log(x, b); }
        public static float Max(float x, float y) { return Math.Max(x, y); }
        public static float Min(float x, float y) { return Math.Min(x, y); }
        public static int Sign(float x) { return Math.Sign(x); }
        public static float Truncate(float x) { return (float)Math.Truncate(x); }
    }
}

/// <summary>
/// Static helpers replacing netstandard2.1-only BCL APIs used by the vendored DotRecast subset.
/// </summary>
internal static class RcBackport
{
    public static int Clamp(int value, int min, int max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    public static float Clamp(float value, float min, float max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    public static void Fill<T>(T[] array, T value)
    {
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = value;
        }
    }

    public static void Fill<T>(T[] array, T value, int start, int count)
    {
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            array[i] = value;
        }
    }

    public static bool IsFinite(float x)
    {
        return !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
