using System.Threading;
using System.Threading.Tasks;

namespace Shared
{
    public class PauseTokenSource
    {
        public bool IsPaused
        {
            get { return _paused != null; }
            set
            {
                if (value)
                {
                    // can not fix the warning other then disabling it (https://msdn.microsoft.com/en-us/library/4bw5ewxy.aspx)
#pragma warning disable 420
                    Interlocked.CompareExchange(
                        ref _paused, new TaskCompletionSource<bool>(), null);
#pragma warning restore 420
                }
                else
                {
                    while (true)
                    {
                        var tcs = _paused;
                        if (tcs == null) return;
                        // can not fix the warning other then disabling it (https://msdn.microsoft.com/en-us/library/4bw5ewxy.aspx)
#pragma warning disable 420
                        if (Interlocked.CompareExchange(ref _paused, null, tcs) != tcs) continue;
#pragma warning restore 420
                        tcs.SetResult(true);
                        break;
                    }
                }
            }
        }
        public PauseToken Token { get { return new PauseToken(this); } }

        private volatile TaskCompletionSource<bool> _paused;

        internal Task WaitWhilePausedAsync()
        {
            var cur = _paused;
            return cur != null ? cur.Task : CompletedTask;
        }

        internal static readonly Task CompletedTask = Task.FromResult(true);

    }

    public struct PauseToken
    {
        private readonly PauseTokenSource _source;
        internal PauseToken(PauseTokenSource source) { _source = source; }

        public bool IsPaused { get { return _source != null && _source.IsPaused; } }

        public Task WaitWhilePausedAsync()
        {
            return IsPaused ?
                _source.WaitWhilePausedAsync() :
                PauseTokenSource.CompletedTask;
        }
    }
}
