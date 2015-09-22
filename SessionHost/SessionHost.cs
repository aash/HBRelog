using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using HonorbuddyClient;
using Shared;
using WowClient;
using WowClient.Lua;

namespace Fenix
{

    public interface ISessionHostService
    {
        void StartSession(string sessionName);
        void PauseSession(string sessionName);
        void ResumeSession(string sessionName);
        void KillSession(string sessionName);
        void StopSession(string sessionName);
        void CreateSession(string sessionName, Session session);
        void RemoveSession(string sessionName);
    }

    [Serializable]
    public sealed class SessionHostConfig
    {
        private static readonly Lazy<SessionHostConfig> LazyInstance =
            new Lazy<SessionHostConfig>(() => new SessionHostConfig());

        public static SessionHostConfig Instance { get { return LazyInstance.Value; } }

        private SessionHostConfig()
        {
        }

        public void Load()
        { }

        public void Save()
        {}

        public string WowExePath { get; set; }
        public string WowExeArgs { get; set; }
        [XmlIgnore]
        public string[] HonorbuddyKeyPool { get; set; }
        public string HonorbuddyKeyPoolData { get; set; }
    }

    public interface ISessionHost
    {
        // session host provides list of sessions
        // which can be edited add/remove sessions
        HonorbuddyKeyPool HonorbuddyKeyPool { get; set; }
        string HonorbuddyExePath { get; set; }
        string WowExePath { get; set; }
        string WowExeArgs { get; set; }
        string DefaultCombatRoutine { get; set; }
        void StartSession(string sessionName);
        void StopSession(string sessionName);
    }

    class SessionHostSevice
    {

        public void Initialize()
        {
            
        }
    }

    public class WowProcessPool : IDisposable, IChildItem<SessionHost>
    {

        private readonly List<Process> _processes;
        private readonly List<Process> _freeProcessPool;

        public WowProcessPool(SessionHost parent)
        {
            ParentObject = parent;
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
            var proc = Process.Start(ParentObject.WowExePath, ParentObject.WowExeArgs);
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

        public SessionHost ParentObject { get; internal set; }

        SessionHost IChildItem<SessionHost>.Parent
        {
            get { return ParentObject; }
            set { ParentObject = value; }
        }
        public SessionHost  Parent { get; set; }
    }


    /// <summary>
    /// Consolidates sessions, 
    /// </summary>
    [Serializable]
    public class SessionHost : ISessionHost, IDisposable
    {
        private readonly WowProcessPool _wowProcessPool;
        public WowProcessPool WowProcessPool { get { return _wowProcessPool; }}
        public HonorbuddyKeyPool HonorbuddyKeyPool { get; set; }
        public WowCredentialsRepository WowCredentials { get; set; }

        [XmlIgnore]
        public string SessionXmlLocation { get; private set; }

        [XmlIgnore]
        public static string SessionXmlFileExtension = ".session";

        public string HonorbuddyExePath { get; set; }
        public string WowExePath { get; set; }
        public string WowExeArgs { get; set; }
        public string DefaultCombatRoutine { get; set; }

        private string GetSessionXmlPath(string sessionName)
        {
            return Path.Combine(SessionXmlLocation, sessionName + SessionXmlFileExtension);
        }

        private Session LoadSession(string xmlFilePath)
        {
            Session result;
            using (var reader = XmlReader.Create(xmlFilePath))
            {
                result = (Session)(new XmlSerializer(typeof(Session))
                    .Deserialize(reader));
            }
            return result;
        }

        private void SaveSession(string xmlFilePath, Session session)
        {
            using (var writer = XmlWriter.Create(xmlFilePath,
                new XmlWriterSettings() { Indent = true }))
            {
                var serializer = new XmlSerializer(typeof(Session));
                serializer.Serialize(writer, session);
            }
        }

        public void StartSession(string sessionName)
        {
            throw new NotImplementedException();
        }

        public void StopSession(string sessionName)
        {
            throw new NotImplementedException();
        }

        [XmlElement(ElementName = "Session")]
        public ChildItemCollection<SessionHost, Session> Sessions { get; set; }

        public SessionHost()
        {
            Sessions = new ChildItemCollection<SessionHost, Session>(this);
            _wowProcessPool = new WowProcessPool(this);
            _wowProcessPool.InitializeAsync().Wait();
        }

        public void Dispose()
        {
            _wowProcessPool.Dispose();
        }
    }

    [Serializable]
    [XmlInclude(typeof(LoginSessionTask))]
    [XmlInclude(typeof(LogoutSessionTask))]
    [XmlInclude(typeof(GeneralSessionTask))]
    abstract public class SessionTask : IChildItem<Session>
    {
        abstract public Task<bool> RunAsync(CancellationToken cancel, PauseToken pause);
        [XmlIgnore]
        public Session ParentObject { get; internal set; }
        Session IChildItem<Session>.Parent
        {
            get { return ParentObject; }
            set { ParentObject = value; }
        }
    }

    [Flags]
    public enum TaskStopTrigger
    {
        Timeout = 1,
        BotStop = 2,
        Custom = 4,
    }

    [Serializable]
    public class LoginSessionTask : SessionTask
    {
        public string CharacterName { get; set; }
        [XmlIgnore]
        public bool IsCompleted { get; private set; }

        public override async Task<bool> RunAsync(CancellationToken cancel, PauseToken pause)
        {
            int WowGetIntoGameMaxRetry = 10;

            IsCompleted = false;

            // get into the game
            // retry pattern
            int retryCount = 0;
            WowCredential credUpd = null;
            WowCredential cred = ParentObject.ParentObject.WowCredentials[CharacterName];
            while (credUpd == null
                   && retryCount < WowGetIntoGameMaxRetry)
            {
                credUpd = await ParentObject.Wow.GetIntoTheGameAsync(cred, cancel, pause);
                retryCount++;
                Console.WriteLine("Wow.GetIntoTheGameAsync retry {0}", retryCount);
            }
            if (retryCount >= WowGetIntoGameMaxRetry || credUpd == null)
            {
                Console.WriteLine("could not get into the game, retry count overflow");
                return false;
            }
            IsCompleted = true;

            return true;
        }
    }
    [Serializable]

    public class LogoutSessionTask : SessionTask
    {
        [XmlIgnore]
        public bool IsCompleted { get; private set; }

        public override async Task<bool> RunAsync(CancellationToken cancel, PauseToken pause)
        {
            int WowLogoutMaxRetry = 10;

            IsCompleted = false;

            // logout
            // retry pattern
            int retryCount = 0;
            if (ParentObject.Wow.IsInGame)
            {
                while (!await ParentObject.Wow.LogoutAsync(new WowCredential(), cancel, pause)
                       && retryCount < WowLogoutMaxRetry)
                {
                    retryCount++;
                    Console.WriteLine("Wow.LogoutAsync retry {0}", retryCount);
                }
                if (retryCount >= WowLogoutMaxRetry)
                {
                    Console.WriteLine("could not logout, retry count overflow");
                    return false;
                }
            }
            IsCompleted = true;

            return true;
        }
    }

    [Serializable]
    public class GeneralSessionTask : SessionTask
    {
        public string CharacterName { get; set; }
        public string BotName { get; set; }
        public string ProfilePath { get; set; }
        public string CombatRoutine { get; set; }
        public TaskStopTrigger StopTrigger { get; set; }
        // TODO workaround unserializable TimeSpan
        public TimeSpan Timeout { get; set; }

        private readonly AsyncAutoResetEvent _taskCompletedEvent;
        private CancellationTokenSource _cancelIfInvalidState;
        [XmlIgnore]
        public bool IsCompleted { get; private set; }

        public GeneralSessionTask()
        {
            _taskCompletedEvent = new AsyncAutoResetEvent();
        }

        public override async Task<bool> RunAsync(CancellationToken cancel, PauseToken pause)
        {
            int WowGetIntoGameMaxRetry = 10;
            int HonorbuddyCreateMaxRetry = 10;

            IsCompleted = false;

            // get into the game
            // retry pattern
            int retryCount = 0;
            WowCredential credUpd = null;
            WowCredential cred = ParentObject.ParentObject.WowCredentials[CharacterName];
            while (credUpd == null
                   && retryCount < WowGetIntoGameMaxRetry)
            {
                credUpd = await ParentObject.Wow.GetIntoTheGameAsync(cred, cancel, pause);
                retryCount++;
                Console.WriteLine("Wow.GetIntoTheGameAsync retry {0}", retryCount);
            }
            if (retryCount >= WowGetIntoGameMaxRetry || credUpd == null)
            {
                Console.WriteLine("could not get into the game, retry count overflow");
                return false;
            }

            // updated character list is always extension to input
            if (credUpd.Characters.Count() != cred.Characters.Count)
            {
                
            }

            HonorbuddyProxy honorbuddy = null;

            // start honorbuddy and prepare proxy
            // retry pattern
            retryCount = 0;
            if (ParentObject.ParentObject.HonorbuddyKeyPool.IsEmpty)
                return false;
            while ((honorbuddy = await HonorbuddyProxy.Create(
                ParentObject.ParentObject.HonorbuddyExePath,
                ParentObject.ParentObject.HonorbuddyKeyPool.Allocate(),
                ParentObject.Wow.WowProcess.Id,
                CombatRoutine)) == null && retryCount < HonorbuddyCreateMaxRetry)
            {
                retryCount++;
                Console.WriteLine("HonorbuddyProxy.Create retry {0}", retryCount);
            }
            if (retryCount >= HonorbuddyCreateMaxRetry || honorbuddy == null)
            {
                Console.WriteLine("could not start honorbuddyproxy, retry count overflow");
                return false;
            }

            honorbuddy.Disposed += HonorbuddyOnDisposed;
            honorbuddy.Event += HonorbuddyOnEvent;

            if (!honorbuddy.StartBot(BotName, ProfilePath))
            {
                Console.WriteLine("could not complete StartBot botname: {0}, profilepath: {1}", BotName, ProfilePath);
                return false;
            }

            _cancelIfInvalidState = CancellationTokenSource.CreateLinkedTokenSource(cancel);


            // will wait for fail or bot stopped event
            var ff = await _taskCompletedEvent.WaitOne(_cancelIfInvalidState.Token);
            if (IsCompleted)
                honorbuddy.Dispose();
            return ff;
        }

        private void HonorbuddyOnDisposed(object sender, EventArgs eventArgs)
        {
            if (!IsCompleted)
                _cancelIfInvalidState.Cancel();
        }

        private void HonorbuddyOnEvent(object sender, HonorbuddybEventArgs e)
        {
            switch (e.Name)
            {
                case "OnShutdownRequested":
                    _cancelIfInvalidState.Cancel();
                    break;
                case "OnBotStopped":
                    if (StopTrigger == TaskStopTrigger.BotStop)
                    {
                        IsCompleted = true;
                        _taskCompletedEvent.Set();
                    }
                    break;
            }
        }
    }

    public interface ISession
    {
        Task<bool> Run(CancellationToken cancel, PauseToken pause);
        void Start();
        void Resume();
        void Pause();
        void Stop();
    }

    [Serializable]
    public class Session : IDisposable, IChildItem<SessionHost>
    {
        internal WowWrapper Wow { get; private set; }
        private readonly CancellationTokenSource _cancelTokenSource;
        private readonly PauseTokenSource _pauseTokenSource;
        private readonly CancellationToken _cancelToken;
        private readonly PauseToken _pauseToken;
        private Task<bool> _runTask;

        public string Name { get; set; }
        //public Queue<SessionTask> Tasks { get; set; }
        [XmlElement(ElementName = "Task")]
        public ChildItemCollection<Session, SessionTask> Tasks { get; set; }

        public Session()
        {
            _cancelTokenSource = new CancellationTokenSource();
            _cancelToken = _cancelTokenSource.Token;
            _pauseTokenSource = new PauseTokenSource();
            _pauseToken = _pauseTokenSource.Token;
            Wow = new WowWrapper();
            Tasks = new ChildItemCollection<Session, SessionTask>(this);
            _runTask = null;
        }

        private async Task<bool> RestoreWowProcess(CancellationToken cancel, PauseToken pause)
        {
            int retryCount = 0;
            int WowAllocateMaxRetry = 10;
            int WowAttachMaxRetry = 10;

            Process wowProcess = null;
            if (Wow.WowProcess == null || Wow.WowProcess.HasExited)
            {
                // allocate wow process
                // retry pattern
                while (
                    (wowProcess = await ParentObject.WowProcessPool.AllocateAsync(cancel, pause)) == null
                    && retryCount < WowAllocateMaxRetry)
                {
                    retryCount++;
                    Console.WriteLine("_wowProcessPool.AllocateAsync retry {0}", retryCount);
                }
                if (retryCount >= WowAllocateMaxRetry)
                {
                    Console.WriteLine("can't AllocateAsync wow process, retry count overflow");
                    return false;
                }
            }
            else
            {
                wowProcess = Wow.WowProcess;
            }

            // attach to wow process
            // retry pattern
            retryCount = 0;
            while (
                !await Wow.AttachToProcessAsync(wowProcess, cancel, pause)
                && retryCount < WowAttachMaxRetry)
            {
                retryCount++;
                Console.WriteLine("Wow.AttachToProcessAsync retry {0}", retryCount);
            }
            if (retryCount >= WowAttachMaxRetry)
            {
                Console.WriteLine("could not attach to wow process {0}, retry count overflow", wowProcess.Id);
                return false;
            }

            return true;
        }

        private async Task<bool> RunAsync(CancellationToken cancel, PauseToken pause)
        {
            if (Tasks == null || Tasks.Count == 0)
                return true;

            if (!await RestoreWowProcess(cancel, pause))
                return false;

            var taskQueue = new Queue<SessionTask>(Tasks.ToList());
            while (taskQueue.Any())
            {
                if (cancel.IsCancellationRequested)
                    return false;
                Console.WriteLine("pick next task");
                var task = taskQueue.Dequeue();
                var retryCount = 0;
                var Max = 10;
                while (!await task.RunAsync(cancel, pause) && retryCount < Max)
                {
                    await RestoreWowProcess(cancel, pause);
                    retryCount++;
                    Console.WriteLine("task.RunAsync retry {0}", retryCount);
                }
                if (retryCount >= Max)
                {
                    Console.WriteLine("could not perform session task, retry count overflow");
                }
                else
                {
                    Console.WriteLine("task finished");
                }
            }
            return true;
        }

        public void Start()
        {
            if (_runTask != null)
                return;
            _runTask = RunAsync(_cancelToken, _pauseToken);
        }

        public void Stop()
        {
            Resume();
            _cancelTokenSource.Cancel();
        }

        public void Pause()
        {
            _pauseTokenSource.IsPaused = true;
        }

        public void Resume()
        {
            _pauseTokenSource.IsPaused = false;
        }

        public void Dispose()
        {
            Wow.Dispose();
            _cancelTokenSource.Dispose();

        }

        [XmlIgnore]
        public SessionHost ParentObject { get; internal set; }

        SessionHost IChildItem<SessionHost>.Parent
        {
            get { return ParentObject; }
            set { ParentObject = value; }
        }
    }
}
