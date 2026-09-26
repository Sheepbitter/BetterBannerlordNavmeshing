using System;

namespace DotRecast.Core
{
    // BACKPORTED for net472/C# 7.3: originally `readonly ref struct` (no IDisposable). C# 7.3 cannot use
    // pattern-based disposal on a ref struct, so it is a plain struct implementing IDisposable.
    public struct RcScopedTimer : IDisposable
    {
        private readonly RcContext _context;
        private readonly RcTimerLabel _label;

        internal RcScopedTimer(RcContext context, RcTimerLabel label)
        {
            _context = context;
            _label = label;
            
            _context.StartTimer(_label);
        }

        public void Dispose()
        {
            _context.StopTimer(_label);
        }
    }
}