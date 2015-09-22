﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
//using GreyMagic;
using HighVoltz.HBRelog.FiniteStateMachine;
using HighVoltz.HBRelog.FiniteStateMachine.FiniteStateMachine;
using HighVoltz.HBRelog.Settings;
//using HighVoltz.HBRelog.WoW.FrameXml;
//using HighVoltz.HBRelog.WoW.Lua;
using HighVoltz.HBRelog.WoW.States;
//using Region = HighVoltz.HBRelog.WoW.FrameXml.Region;
using WowClient;
using WowSettings = HighVoltz.HBRelog.Settings.WowSettings;

namespace HighVoltz.HBRelog.WoW
{

    public sealed class WowManager
        
        // : Engine, IGameManager
    {
        public WowManager(CharacterProfile profile)
        {
            Profile = profile;
            Wrapper = new WowWrapper();
        }

        #region Fields

        private readonly object _lockObject = new object();
        internal readonly Stopwatch LoginTimer = new Stopwatch();

        //public WowLuaManager LuaManager;

        private bool _isExiting;
        //private GlueState _lastGlueStatus = GlueState.None;
        private DateTime _throttleTimeStamp = DateTime.Now;
        internal bool ProcessIsReadyForInput;
        private CharacterProfile _profile;

        private int _windowCloseAttempt;
        private Timer _wowCloseTimer;


        #endregion

        #region Properties

        public WowSettings Settings { get; private set; }

        public WowWrapper Wrapper;

        public bool InGame { get; private set; }
        public Process GameProcess { get; internal set; }

        public Process ReusedGameProcess { get; internal set; }

        public WowLockToken LockToken { get; internal set; }

        public bool ServerIsOnline
        {
            get { return !HbRelogManager.Settings.CheckRealmStatus || HbRelogManager.WowRealmStatus.RealmIsOnline(Settings.ServerName, Settings.Region); }
        }

        public bool Throttled
        {
            get
            {
                var time = DateTime.Now;
                var ret = time - _throttleTimeStamp < TimeSpan.FromSeconds(HbRelogManager.Settings.LoginDelay);
                if (!ret)
                    _throttleTimeStamp = time;
                return ret;
            }
        }

        public bool IsReusedGameProcess
        {
            get { return Settings.ReuseFreeWowProcess && ReusedGameProcess != null; }
        }

        public bool IsOwnGameProcess
        {
            get { return !IsReusedGameProcess; }
        }


        #endregion

        #region IGameManager Members

        public CharacterProfile Profile
        {
            get { return _profile; }
            private set
            {
                _profile = value;
                Settings = value.Settings.WowSettings;
            }
        }

        public void SetSettings(WowSettings settings)
        {
            Settings = settings;
        }

        /// <summary>
        ///     Character is logged in game
        /// </summary>

        public bool StartupSequenceIsComplete { get; internal set; }
        public event EventHandler<ProfileEventArgs> OnStartupSequenceIsComplete;

        public void Start()
        {
            IsRunning = true;
            IsReadyToMonitor = false;
            var task = Run();
        }

        public async Task<bool> Run()
        {
            var proc = await HbRelogManager.WowProcessPool.AllocateAsync();
            if (proc == null)
            {
                return false;
            }
            GameProcess = proc;
            if (!await Wrapper.AttachToProcessAsync(proc))
            {
                return false;
            }
            Log.Write("attached to wow client");
            var s = new WowClient.WowCredential
            {
                Login = Settings.Login,
                Password = Settings.Password,
                Realm = Settings.ServerName,
                CharacterName = Settings.CharacterName,
                AuthenticatorSerial = Settings.AuthenticatorSerial,
                AuthenticatorRestoreCode = Settings.AuthenticatorRestoreCode,
            };
            Log.Write("getting into the game");
            var cred = await Wrapper.GetIntoTheGameAsync(s);
            if (cred == null)
            {
                return false;
            }
            Log.Write("game client ready");
            StartupSequenceIsComplete = true;
            Log.Write("waiting honorbuddy to start before start to monitor");
            await Shared.Utility.WaitUntilAsync(() => IsReadyToMonitor, TimeSpan.FromMinutes(1), 500);
            Log.Write("start to monitor");
            return await MonitorAsync();
        }

        public bool IsReadyToMonitor { get; set; }

        public async Task<bool> MonitorAsync()
        {
            while (true)
            {
                // TODO check resposiveness
                var name = await Wrapper.CurrentCharacterNameAsync();
                if (name != Settings.CharacterName)
                {
                    // TODO terminate
                    return false;
                }
                await Task.Delay(1000);
            }
        }

        public bool IsRunning { get; set; }

        public void Stop()
        {
            // try to aquire lock, if fail then kill process anyways.
            bool lockAquried = Monitor.TryEnter(_lockObject, 500);
            if (IsRunning)
            {
                if (IsOwnGameProcess)
                {
                    CloseGameProcess();
                }
                GameProcess = null;
                ProcessIsReadyForInput = false;
                IsRunning = false;
                StartupSequenceIsComplete = false;
                ReusedGameProcess = null;
                if (LockToken != null)
                {
                    LockToken.Dispose();
                    LockToken = null;
                }
            }
            if (lockAquried)
                Monitor.Exit(_lockObject);
        }

        public void Pulse()
        {
            //lock (_lockObject)
            //{
            //    base.Pulse();
            //}
        }

        #endregion

        #region Functions

        public void SetStartupSequenceToComplete()
        {
            StartupSequenceIsComplete = true;
            Profile.Log("Login sequence complete");
            Profile.Status = "Logged into WoW";
            if (OnStartupSequenceIsComplete != null)
                OnStartupSequenceIsComplete(this, new ProfileEventArgs(Profile));
        }

        public void CloseGameProcess()
        {
            try
            {
                CloseGameProcess(GameProcess);
            }
            // handle the "No process is associated with this object' exception while wow process is still 'active'
            catch (InvalidOperationException ex)
            {
                Log.Err(ex.ToString());
                //if (LuaManager.Memory != null)
                //    CloseGameProcess(Process.GetProcessById(LuaManager.Memory.Process.Id));
            }
            //Profile.TaskManager.HonorbuddyManager.CloseBotProcess();
            GameProcess = null;
        }

        private void CloseGameProcess(Process proc)
        {
            if (!_isExiting && proc != null && !proc.HasExitedSafe())
            {
                _isExiting = true;
                Profile.Log("Attempting to close Wow");
                proc.CloseMainWindow();
                _windowCloseAttempt++;
                _wowCloseTimer = new Timer(
                    state =>
                    {
                        if (!((Process)state).HasExitedSafe())
                        {
	                        if (_windowCloseAttempt++ < 6)
	                        {
		                        proc.CloseMainWindow();
	                        }
	                        else
	                        {
		                        try
		                        {
			                        Profile.Log("Killing Wow");
			                        ((Process) state).Kill();
		                        }
		                        catch {}
	                        }
                        }
                        else
                        {
                            _isExiting = false;
                            Profile.Log("Successfully closed Wow");
                            _wowCloseTimer.Dispose();
                            _windowCloseAttempt = 0;
                        }
                    },
                    proc,
                    1000,
                    1000);
            }
        }


        #endregion

    }
}