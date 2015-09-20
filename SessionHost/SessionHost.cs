using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using HonorbuddyClient;
using Shared;
using WowClient;

namespace SessionHost
{

    public interface ISessionHostService
    {
        void StartSession(string sessionName);
        void PauseSession(string sessionName);
        void ResumeSession(string sessionName);
        void KillSession(string sessionName);
        void StopSession(string sessionName);
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
        List<Session> Sessions { get; }
        HonorbuddyKeyPool HonorbuddyKeyPool { get; }
        string HonorbuddyExePath { get; }
        string WowExePath { get; }
        string WowExeArgs { get; }
        string CombatRoutine { get; }
    }



    /// <summary>
    /// Consolidates sessions, 
    /// </summary>
    [Serializable]
    public class SessionHost : ISessionHost, IDisposable
    {
        private readonly WowProcessPool _wowProcessPool;
        public WowProcessPool WowProcessPool { get { return _wowProcessPool; } }
        public List<Session> SessionList { get; private set; }
        public List<Session> Sessions { get; private set; }
        public HonorbuddyKeyPool HonorbuddyKeyPool { get; private set; }
        public string HonorbuddyExePath { get; private set; }
        public string WowExePath { get; private set; }
        public string WowExeArgs { get; private set; }
        public string CombatRoutine { get; private set; }

        public SessionHost()
        {
            _wowProcessPool = new WowProcessPool(@"C:\hb_home\wow\Wow.exe", @"-noautolaunch64bit");
            _wowProcessPool.InitializeAsync().Wait();
            //    Config.WowExePath, Config.WowExeArgs);
        }

        public void Dispose()
        {
            _wowProcessPool.Dispose();
        }
    }

    [Serializable]
    abstract public class SessionTask
    {
        protected Session ParentSession;

        protected SessionTask(Session session)
        {
            ParentSession = session;
        }

        abstract public Task<bool> RunAsync(CancellationToken cancel, PauseToken pause);
    }

    [Flags]
    public enum TaskStopTrigger
    {
        Timeout = 1,
        BotStop = 2,
        Custom = 4,
    }

    public class GeneralSessionTask : SessionTask
    {
        public string CharacterName { get; set; }
        public string BotName { get; set; }
        public string ProfilePath { get; set; }
        public string CombatRoutine { get; set; }
        public TaskStopTrigger StopTrigger { get; set; }
        public TimeSpan Timeout { get; set; }

        private readonly AsyncAutoResetEvent _taskCompletedEvent;
        private CancellationTokenSource _cancelIfInvalidState;
        private readonly SessionHost _sessionHost;
        public bool IsCompleted { get; private set; }

        public GeneralSessionTask(Session session) : base(session)
        {
            _sessionHost = session.SessionHost;
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
            var cred = new WowCredential()
            {
                Login = "wobblybooz@gmail.com",
                Password = "dfh43kd$sk645*",
                CharacterName = "Алгебраична",
                Realm = "Пиратская бухта",
                AuthenticatorSerial = "EU-1509-1275-8745",
                AuthenticatorRestoreCode = "55748B78MB"
            };
            WowCredential credUpd = null;
            while (credUpd == null
                   && retryCount < WowGetIntoGameMaxRetry)
            {
                credUpd = await ParentSession.Wow.GetIntoTheGameAsync(cred, cancel, pause);
                retryCount++;
                Console.WriteLine("_wowWrapper.GetIntoTheGameAsync retry {0}", retryCount);
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
            if (_sessionHost.HonorbuddyKeyPool.Any)
            .Allocate()
            while ((honorbuddy = await HonorbuddyProxy.Create(_sessionHost.HonorbuddyExePath,
                , ParentSession.Wow.WowProcess.Id,
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

    public class CharacterRepository
    {
        public static WowCredential GetSettings(string characterName)
        {
            return new WowCredential();
        }
    }

    //public class SessionRepository
    //{
    //    public 
    //}

    public interface ISession
    {
        Task<bool> Run(CancellationToken cancel, PauseToken pause);
        void Start();
        void Resume();
        void Pause();
        void Stop();
    }

    [Serializable]
    public class Session : IDisposable
    {
        public SessionHost SessionHost { get; set; }
        public readonly WowWrapper Wow;
        private readonly CancellationTokenSource _cancelTokenSource;
        private readonly PauseTokenSource _pauseTokenSource;
        private readonly WowProcessPool _wowProcessPool;
        private readonly CancellationToken _cancelToken;
        private readonly PauseToken _pauseToken;
        private Task<bool> _runTask;

        public string Name { get; set; }
        public string Character { get; set; }
        public Queue<SessionTask> TaskQueue { get; set; }

        public Session(SessionHost sessionHost)
        {
            SessionHost = sessionHost;
            _wowProcessPool = sessionHost.WowProcessPool;
            _cancelTokenSource = new CancellationTokenSource();
            _cancelToken = _cancelTokenSource.Token;
            _pauseTokenSource = new PauseTokenSource();
            _pauseToken = _pauseTokenSource.Token;
            Wow = new WowWrapper();
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
                    (wowProcess = await _wowProcessPool.AllocateAsync(cancel, pause)) == null
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
                Console.WriteLine("_wowWrapper.AttachToProcessAsync retry {0}", retryCount);
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
            if (TaskQueue == null || TaskQueue.Count == 0)
                return true;

            if (!await RestoreWowProcess(cancel, pause))
                return false;

            var taskQueue = new Queue<SessionTask>(TaskQueue.ToList());
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
    }
}
