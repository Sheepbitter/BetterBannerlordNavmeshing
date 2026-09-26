
// BACKPORTED for net472/C# 7.3 - see ThirdParty/DotRecast/NOTICE.md
using System;
using System.Runtime.CompilerServices;

namespace DotRecast.Core
{
    public static class RcThrowHelper
    {
        public static void ThrowExceptionIfIndexOutOfRange(int index, int size)
        {
            if (0 > index || index >= size)
            {
                throw new IndexOutOfRangeException($"Index {index} is out of range - size({size})");
            }
        }

        public static void ThrowArgumentOutOfRangeException(string argument)
        {
            throw new ArgumentOutOfRangeException(argument);
        }

        public static void ThrowNullReferenceException(string argument)
        {
            throw new NullReferenceException(argument);
        }
        
        public static void ThrowException(string message)
        {
            throw new Exception(message);
        }

    }
}