using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace WowClient
{
    // singleton ??
    public class WowProcessPool : IDisposable
    {

        private readonly string _wowExePath;
        private readonly string[] _wowExeArgs;
        private readonly List<Process> _processes;
        private readonly List<Process> _freeProcessPool;

        public WowProcessPool(string wowExePath, params string[] wowExeArgs)
        {
            _wowExePath = wowExePath;
            _wowExeArgs = wowExeArgs;
            _processes = new List<Process>();
            _freeProcessPool = new List<Process>();
        }

        private async Task<Process> StartNewAsync()
        {
            var proc = Process.Start(_wowExePath, string.Join(" ", _wowExeArgs));
            if (proc == null)
                return null;
            if (!await Utility.WaitUntilAsync(() => proc.MainWindowHandle != IntPtr.Zero, TimeSpan.FromMinutes(2), 100))
            {
                Console.WriteLine("wow process start timeout");
                return null;
            }
            await Task.Delay(1000);
            return proc;
        }

        public async Task<Process> AllocateAsync()
        {
            if (_freeProcessPool.Any())
                return _freeProcessPool.First();
            var proc = await StartNewAsync();
            _processes.Add(proc);
            return proc;
        }

        public void Free(int id)
        {
            if (_freeProcessPool.Any(p => p.Id == id))
                throw new ArgumentException("process is not in use");
            var proc = _processes.FirstOrDefault(p => p.Id == id);
            if (proc == null)
                throw new ArgumentException("process does not belong to the pool");
            _processes.Remove(proc);
            _freeProcessPool.Add(proc);
        }

        public void Dispose()
        {
            foreach (var proc in _processes)
            {
                proc.Kill();
            }
        }
    }
}
