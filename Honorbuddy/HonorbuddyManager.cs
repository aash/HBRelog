﻿/*
Copyright 2012 HighVoltz

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using HighVoltz.HBRelog.FiniteStateMachine;
using HighVoltz.HBRelog.FiniteStateMachine.FiniteStateMachine;
using HighVoltz.HBRelog.Honorbuddy.States;
using HighVoltz.HBRelog.Settings;
using HighVoltz.HBRelog.WoW;
using HighVoltz.HBRelog.WoW.States;
using Microsoft.Win32.SafeHandles;
using MonitorState = HighVoltz.HBRelog.Honorbuddy.States.MonitorState;

namespace HighVoltz.HBRelog.Honorbuddy
{
    public class HonorbuddyManager : Engine, IBotManager
    {
        private Stopwatch _botExitTimer;
        readonly object _lockObject = new object();

        public bool StartupSequenceIsComplete { get; private set; }

        private Stopwatch _lastHeartbeat = new Stopwatch();

        public Stopwatch LastHeartbeat
        {
            get { return _lastHeartbeat; }
        }

        public event EventHandler<ProfileEventArgs> OnStartupSequenceIsComplete;
        CharacterProfile _profile;
        public CharacterProfile Profile
        {
            get { return _profile; }
            private set { _profile = value; Settings = value.Settings.HonorbuddySettings; }
        }
        public HonorbuddySettings Settings { get; private set; }

        private string _hbKeyInUse;

        public Process BotProcess { get; private set; }

	    public bool WaitForBotToExit
	    {
		    get
		    {
                if (Profile.TaskManager.WowManager.GameProcess != null && !Profile.TaskManager.WowManager.GameProcess.HasExitedSafe())
					return false;
                if (BotProcess == null || BotProcess.HasExitedSafe())
					return false;
			    if (_botExitTimer == null)
				    _botExitTimer = Stopwatch.StartNew();
			    return _botExitTimer.ElapsedMilliseconds < 20000;
		    }
	    }

        public HonorbuddyManager(CharacterProfile profile)
        {
            Profile = profile;
            States = new List<State> 
            {
                new UpdateHonorbuddyState(this),
                new StartHonorbuddyState(this),
                new MonitorState(this),
            };
            _hbKeyInUse = "";
        }

        public void SetSettings(HonorbuddySettings settings)
        {
            Settings = settings;
        }

        bool _isExiting;
        public void CloseBotProcess()
        {
            if (!_isExiting && BotProcess != null && !BotProcess.HasExitedSafe())
            {
                _isExiting = true;
                Task.Run(async () => await Utility.CloseBotProcessAsync(BotProcess, Profile))
                    .ContinueWith(o =>
                    {
                        _isExiting = false;
                        BotProcess = null;
                        if (o.IsFaulted)
                            Profile.Log("{0}", o.Exception.Flatten().ToString());
                    });
            }
        }

        public void Start()
        {
            IsRunning = true;
            PickFreeHBKey();
        }

        // TODO add Max sessions handler
        // TODO (Debug) communicate Debug/Release free hb keys list
        public string PickFreeHBKey()
        {
            if (string.IsNullOrEmpty(Settings.HonorbuddyKey))
            {
                if (HbRelogManager.Settings.FreeHBKeyPool.Count > 0)
                {
                    _hbKeyInUse = HbRelogManager.Settings.FreeHBKeyPool.First();
                    HbRelogManager.Settings.FreeHBKeyPool.Remove(_hbKeyInUse);
                }
                return _hbKeyInUse;
            }
            _hbKeyInUse = Settings.HonorbuddyKey;
            return Settings.HonorbuddyKey;
        }
        
        internal void StartHonorbuddy()
        {
	        _botExitTimer = null;
            Profile.Log("starting {0}", Profile.Settings.HonorbuddySettings.HonorbuddyPath);
            Profile.Status = "Starting Honorbuddy";
            StartupSequenceIsComplete = false;
            var hbKey = _hbKeyInUse;
            string hbArgs = string.Format("/noupdate /pid={0} {1}{2}{3}{4}",
                Profile.TaskManager.WowManager.GameProcess.Id,
                !string.IsNullOrEmpty(hbKey) ? string.Format("/hbkey=\"{0}\" ", hbKey) : string.Empty,
                !string.IsNullOrEmpty(Settings.CustomClass) ? string.Format("/customclass=\"{0}\" ", Settings.CustomClass) : string.Empty,
                !string.IsNullOrEmpty(Settings.HonorbuddyProfile) ? string.Format("/loadprofile=\"{0}\" ", Settings.HonorbuddyProfile) : string.Empty,
                !string.IsNullOrEmpty(Settings.BotBase) ? string.Format("/botname=\"{0}\" ", Settings.BotBase) : string.Empty
                );

	        if (!string.IsNullOrEmpty(Settings.HonorbuddyArgs))
		        hbArgs +=  Settings.HonorbuddyArgs.Trim();

            var hbWorkingDirectory = Path.GetDirectoryName(Settings.HonorbuddyPath);
            var procStartI = new ProcessStartInfo(Settings.HonorbuddyPath, hbArgs)
            {
                WorkingDirectory = hbWorkingDirectory
            };
            BotProcess = Process.Start(procStartI);
            HbStartupTimeStamp = DateTime.Now;
            if (BotProcess != null)
            {
                // TODO: (DIRTY HACK) works only for english versions, as this code is dependent on locale
                // if hb key dialog appears try to send Key.Enter
                Utility.SleepUntil(() => !string.IsNullOrEmpty(Process.GetProcessById(BotProcess.Id).MainWindowTitle), TimeSpan.FromSeconds(10));
                if (Process.GetProcessById(BotProcess.Id).MainWindowTitle.ToLower() == "honorbuddy login")
                {
                    while (Process.GetProcessById(BotProcess.Id).MainWindowTitle.ToLower() == "honorbuddy login")
                    {
                        Utility.SendBackgroundKey(Process.GetProcessById(BotProcess.Id).MainWindowHandle, (char)Keys.Enter, false);
                        Thread.Sleep(50);
                    }
                }
            }
        }

        public DateTime HbStartupTimeStamp { get; private set; }

        public override void Pulse()
        {
            lock (_lockObject)
            {
                base.Pulse();
            }
        }

        public void Stop()
        {
            // try to aquire lock, if fail then kill process anyways.
            bool lockAquried = Monitor.TryEnter(_lockObject, 500);
            if (IsRunning)
            {
                if (BotProcess != null)
                {
                    if (!BotProcess.HasExitedSafe())
                        CloseBotProcess();
                    else
                        BotProcess = null;
                }

                LastHeartbeat.Reset();
                IsRunning = false;
                StartupSequenceIsComplete = false;
                // return key to the pool
                if (!String.IsNullOrEmpty(_hbKeyInUse))
                {
                    HbRelogManager.Settings.FreeHBKeyPool.Add(_hbKeyInUse);
                    _hbKeyInUse = "";
                }
                
            }
            if (lockAquried) // release lock if it was aquired
                Monitor.Exit(_lockObject);
        }


        public void SetStartupSequenceToComplete()
        {
            StartupSequenceIsComplete = true;
            LastHeartbeat.Restart();

            if (HbRelogManager.Settings.MinimizeHbOnStart)
                NativeMethods.ShowWindow(BotProcess.MainWindowHandle, NativeMethods.ShowWindowCommands.Minimize);
            if (OnStartupSequenceIsComplete != null)
                OnStartupSequenceIsComplete(this, new ProfileEventArgs(Profile));
        }

        /// <summary>
        /// returns false if the WoW user interface is not responsive for 10+ seconds.
        /// </summary>
        static internal class HBStartupManager
        {
            private static readonly object LockObject = new object();
            private static readonly Dictionary<string, DateTime> TimeStamps = new Dictionary<string, DateTime>();

            public static bool CanStart(string path)
            {
                var key = path.ToUpper();
                lock (LockObject)
                {
                    if (TimeStamps.ContainsKey(key) &&
                        DateTime.Now - TimeStamps[key] < TimeSpan.FromSeconds(HbRelogManager.Settings.HBDelay))
                    {
                        return false;
                    }
                    TimeStamps[key] = DateTime.Now;
                }
                return true;
            }
        }
    }
}
