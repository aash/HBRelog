using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shared;
using WowClient.Lua;

namespace WowClient
{
    // singleton ??
    public class WowProcessPool : IDisposable
    {

        private readonly string _wowExePath;
        private readonly string _wowExeArgs;
        private readonly List<Process> _processes;
        private readonly List<Process> _freeProcessPool;

        public WowProcessPool(string wowExePath, string wowExeArgs)
        {
            _wowExePath = wowExePath;
            _wowExeArgs = wowExeArgs;
            _processes = new List<Process>();
            _freeProcessPool = new List<Process>();
        }

        public async Task<bool> InitializeAsync()
        {
            foreach (var proc in Process.GetProcessesByName("wow"))
            {
                var wrapper = new WowWrapper();
                if (!await wrapper.AttachToProcessAsync(proc))
                    continue;
                _freeProcessPool.Add(proc);
                wrapper.Dispose();
            }
            return true;
        }

        private async Task<Process> StartNewAsync()
        {
            return await StartNewAsync(new CancellationToken(), new PauseToken());
        }

        private async Task<Process> StartNewAsync(CancellationToken cancel, PauseToken pause)
        {
            var proc = Process.Start(_wowExePath, _wowExeArgs);
            if (proc == null)
                return null;
            if (!await Utility.WaitUntilAsync(() => proc.MainWindowHandle != IntPtr.Zero, cancel, pause, TimeSpan.FromMinutes(2), 100))
            {
                if (!cancel.IsCancellationRequested)
                    Console.WriteLine("wow process start timeout");
                return null;
            }
            return proc;
        }

        public async Task<Process> AllocateAsync()
        {
            return await AllocateAsync(new CancellationToken(), new PauseToken());
        }

        public async Task<Process> AllocateAsync(string characterName, string realm)
        {
            return await AllocateAsync(CancellationToken.None, new PauseToken(), characterName, realm);
        }

        public async Task<Process> AllocateAsync(CancellationToken cancel, PauseToken pause, string characterName, string realm)
        {
            if (_freeProcessPool.Any())
            {
                foreach (var p in _freeProcessPool)
                {
                    var wrapper = new WowWrapper();
                    if (!await wrapper.AttachToProcessAsync(p, cancel, pause))
                    {
                        wrapper.Dispose();
                        continue;
                    }
                    var characterName1 = await wrapper.CurrentCharacterNameAsync();
                    var value = wrapper.Globals.GetValue("realm");
                    string realm1 = null;
                    if (value != null && value.Type == LuaType.String)
                        realm1 = value.String.Value;
                    if (string.IsNullOrEmpty(characterName1) || string.IsNullOrEmpty(realm1))
                        continue;
                    if (characterName == characterName1 && realm == realm1)
                        return p;
                }
                return _freeProcessPool.First();
            }
            var proc = await StartNewAsync(cancel, pause);
            _processes.Add(proc);
            return proc;
        }

        public async Task<Process> AllocateAsync(CancellationToken cancel, PauseToken pause)
        {
            if (_freeProcessPool.Any())
                return _freeProcessPool.First();
            var proc = await StartNewAsync(cancel, pause);
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
