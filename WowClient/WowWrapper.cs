using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WowClient.Lua;
using WowClient.Lua.UI;
using Shared;
using WinAuth;
using Button = WowClient.Lua.UI.Button;

namespace WowClient
{
    using Keys = System.Windows.Forms.Keys;

    public class WowWrapper : IDisposable
    {
        public WowWrapper()
        {}

        private async Task<IAbsoluteAddress> FindPatternAsync(string pattern, int timeoutMilliseconds = 60000, int pollDelayMilliseconds = 500)
        {
            IAbsoluteAddress address = null;
            if (!await Utility.WaitUntilAsync(() =>
            {
                address = Memory.FindPattern(pattern);
                return !address.Equals(null);
            }, timeoutMilliseconds, pollDelayMilliseconds) || address == null)
            {
                Console.WriteLine("pattern find timeout");
                return null;
            }
            return address;
        }

        public async Task<bool> AttachToProcessAsync(Process process)
        {
            return await AttachToProcessAsync(process, new CancellationToken(), new PauseToken());
        }

        public async Task<bool> AttachToProcessAsync(Process process, CancellationToken cancelToken, PauseToken pauseToken)
        {
            // TODO check process is valid
            try
            {
                WowProcess = process;
                Memory = new ReadOnlyMemory(process);

                IAbsoluteAddress address;
                if ((address = await FindPatternAsync(WowPatterns.LuaStatePattern)) == null)
                {
                    Console.WriteLine("memory state initialize timeout");
                    return false;
                }
                LuaStateAddress = address.Deref(2).Deref();

                if ((address = await FindPatternAsync(WowPatterns.GameStatePattern)) == null)
                {
                    Console.WriteLine("memory state initialize timeout");
                    return false;
                }
                GameStateAddress = address.Deref(2);

                // it is actually a reference to where address to focused widget is located
                // so to get address of focused widget we should deref this reference
                if ((address = await FindPatternAsync(WowPatterns.FocusedWidgetPattern)) == null)
                {
                    Console.WriteLine("memory state initialize timeout");
                    return false;
                }
                FocusedWidgetAddressRef = address.Deref(2);

                if ((address = await FindPatternAsync(WowPatterns.LoadingScreenEnableCountPattern)) == null)
                {
                    Console.WriteLine("memory state initialize timeout");
                    return false;
                }
                LoadingScreenEnableCountAddress = address.Deref(2);

                if ((address = await FindPatternAsync(WowPatterns.GlueStatePattern)) == null)
                {
                    Console.WriteLine("memory state initialize timeout");
                    return false;
                }
                GlueStateAddress = address.Deref(2);

                switch (CurrentGlueState)
                {
                    case GlueState.Disconnected:
                        if (!await WaitGlobalsInitAsync(cancelToken, pauseToken))
                        {
                            Console.WriteLine("globals init timout");
                            return false;
                        }
                        if (!await Utility.WaitUntilAsync(async () =>
                        {
                            ResetGlobals();
                            var w = await GetWidgetAsync<EditBox>("AccountLoginAccountEdit");
                            if (w != null)
                                return w.IsVisible && w.IsShown;
                            return false;
                        }, cancelToken, pauseToken, 60000, 500))
                        {
                            Console.WriteLine("login screen init timeout");
                            return false;
                        }
                        break;
                    case GlueState.Updater:
                        return false;
                }
            }
            catch (Exception e)
            {
                throw new Exception("could not attach WowWrapper", e);
            }
            return true;
        }

        private long WalkGlobals()
        {
            long cnt = 0;
            try
            {
                if (Globals == null)
                    return 0;
                foreach (var node in Globals.Nodes)
                {
                    var n = node;
                    while (n != null)
                    {
                        if (n.Value.Type == LuaType.String || n.Value.Type == LuaType.Table)
                            cnt += 1;
                        n = n.Next;
                    }
                }
            }
            catch (Exception e)
            {
                ResetGlobals();
                Console.WriteLine(e);
                return 0;
            }
            return cnt;
        }
        
        private async Task<bool> WaitGlobalsInitAsync(CancellationToken cancel, PauseToken pause)
        {
            long old_cnt = 0;
            if (!await Utility.WaitUntilAsync(() =>
            {
                ResetGlobals();
                long cnt = WalkGlobals();
                if (cnt == 0)
                    return false;
                if (old_cnt == cnt)
                    return true;
                old_cnt = cnt;
                return false;
            }, cancel, pause, 60000, 500))
            {
                Console.WriteLine("globals init timeout");
                return false;
            }
            return true;
        }

        public Process WowProcess { get; private set; }
        private IAbsoluteAddress GameStateAddress { get; set; }
        private IAbsoluteAddress LuaStateAddress { get; set; }

        private IAbsoluteAddress FocusedWidgetAddressRef { get; set; }
        public IAbsoluteAddress FocusedWidgetAddress {
            get { return FocusedWidgetAddressRef.Deref(); } }
        private IAbsoluteAddress LoadingScreenEnableCountAddress { get; set; }

        private IAbsoluteAddress GlueStateAddress { get; set; }

        internal const int LuaStateGlobalsOffset = 0x50;
        public IReadOnlyMemory Memory { get; set; }
        public string ActiveCharacterName { get; set; }

        public UIObject FocusedWidget
        {
            get
            {
                return GetWidget(FocusedWidgetAddress);
            }
        }

        public GlueState CurrentGlueState
        {
            get
            {
                return (GlueState)GlueStateAddress.Deref<int>();
            }
        }

        private LuaTable _globals;
        public LuaTable Globals
        {
            get
            {
                if (Memory == null)
                    return null;
                if (LuaStateAddress.Value == IntPtr.Zero)
                    return null;
                var globalsAddress = LuaStateAddress.Deref(LuaStateGlobalsOffset);
                if (globalsAddress.Value == IntPtr.Zero)
                    return null;
                if (_globals == null || !_globals.Address.Equals(globalsAddress))
                    _globals = new LuaTable(Memory, globalsAddress);
                return _globals;
            }
            set { _globals = value; }
        }

        public void ResetGlobals()
        {
            LuaStateAddress = Memory.FindPattern(WowPatterns.LuaStatePattern)
                .Deref(2)
                .Deref();
            _globals = null;
        }

        public static class WowPatterns
        {
            public const string GlueStatePattern =
                "83 3d ?? ?? ?? ?? ?? 75 ?? e8 ?? ?? ?? ?? 8b 10 8b c8 ff 62 5c c3";
            public const string GameStatePattern =
                "80 3d ?? ?? ?? ?? ?? 74 ?? 50 b9 ?? ?? ?? ?? e8 ?? ?? ?? ?? 85 c0 74 ?? 8b 40 08 83 f8 02 74 ?? 83 f8 01 75 ?? b0 01 c3 32 c0 c3";
            // ref - FrameXML:EditBox:HasFocus
            public const string FocusedWidgetPattern =
                "3b 05 ?? ?? ?? ?? 0f 94 c1 51 ff 75 08 e8 ?? ?? ?? ?? 33 c0 83 c4 10 40 5d c3";
            // ref - Framescript_ExecuteBuffer.
            public const string LuaStatePattern =
                "8b 35 ?? ?? ?? ?? 33 db 57 3b c3 74 ?? 88 18 ff 75 08 8d 85 dc fe ff ff 68 ?? ?? ?? ?? 68 ?? ?? ?? ?? 50";
            // first offset used in 'LoadingScreenEnable' function. This function also fires the 'LOADING_SCREEN_ENABLED' Wrapper event.
            public const string LoadingScreenEnableCountPattern =
                "ff 05 ?? ?? ?? ?? 83 3d ?? ?? ?? ?? ?? 53 56 57 0f 8f ?? ?? ?? ?? 6a 00 e8 ?? ?? ?? ?? 59 e8 ?? ?? ?? ?? 84 c0 74 ?? 6a 00 68";
        }

        public void Dispose()
        {
            if (Memory != null)
                Memory.Dispose();
        }

        public UIObject GetWidget(IAbsoluteAddress address)
        {
            UIObject w = null;
            try
            {
                w = UIObject.Get(this, address);
            }
            catch (Exception e)
            {
                Console.WriteLine("cound not get widget @{0}, message: {1}", address, e);
                return null;
            }
            return w;
        }

        public T GetWidget<T>(IAbsoluteAddress address) where T : UIObject
        {
            return (T)GetWidget(address);
        }

        public async Task<IAbsoluteAddress> GetWidgetAddressAsync(string name, int timeoutMilliseconds = 5000)
        {
            IAbsoluteAddress address;
            bool needRetry = false;
            try
            {
                address = UIObject.GetAddress(this, name);
            }
            catch (Exception)
            {
                address = null;
                needRetry = true;
            }

            if (!needRetry)
                return address;

            var t = Stopwatch.StartNew();
            var isNotTimeout = true;
            while (address == null && isNotTimeout)
            {
                await Task.Delay(100);
                ResetGlobals();
                try
                {
                    address = UIObject.GetAddress(this, name);
                }
                catch (Exception)
                {
                    address = null;
                }
                isNotTimeout = t.ElapsedMilliseconds < timeoutMilliseconds;
            }
            return address;
        }

        public async Task<T> GetWidgetAsync<T>(string name, int timeoutMilliseconds = 1000) where T : UIObject
        {
            IAbsoluteAddress address = await GetWidgetAddressAsync(name, timeoutMilliseconds);
            if (address == null || address.IsNull)
                return null;
            return (T)GetWidget(address);
        }

        public async Task<IEnumerable<T>> GetWidgetsAsync<T>(int timeoutMilliseconds = 1000) where T : UIObject
        {
            var r = await GetWidgetsAsync(timeoutMilliseconds);
            return r.OfType<T>();
        }

        public async Task<IEnumerable<UIObject>> GetWidgetsAsync(int timeoutMilliseconds = 1000)
        {
            IEnumerable<UIObject> widgets;
            bool needRetry = false;
            try
            {
                var addresses = UIObject.GetAllAddresses(this);
                widgets = addresses
                    .Select(GetWidget);
            }
            catch (Exception)
            {
                widgets = null;
                needRetry = true;
            }

            if (!needRetry)
                return widgets;

            var t = Stopwatch.StartNew();
            var isNotTimeout = true;
            while (widgets == null && isNotTimeout)
            {
                await Task.Delay(100);
                ResetGlobals();
                try
                {
                    var addresses = UIObject.GetAllAddresses(this);
                    widgets = addresses
                        .Select(GetWidget);
                }
                catch
                {
                    widgets = null;
                }
                isNotTimeout = t.ElapsedMilliseconds < timeoutMilliseconds;
            }
            return widgets;
        }

        private IScreen GetCurrnetScreen()
        {
            // determine state and return proper screen
            //return new LoginScreen(this);
            return null;
        }

        public IScreen Current {
            get
            {
                return GetCurrnetScreen();
            }
        }

        public async Task<bool> TypeIntoEditBoxAsync(string editBoxName, string text)
        {
            var editBox = await GetWidgetAsync<EditBox>(editBoxName);

            if (editBox == null || !editBox.IsVisible || !editBox.IsEnabled)
            {
                Console.WriteLine("editbox is not valid");
                return false;
            }

            if (editBox.Text == text)
            {
                Console.WriteLine("editbox already got that text");
                return true;
            }

            if (!editBox.HasFocus && !await FocusEditBoxAsync(editBoxName))
            {
                Console.WriteLine("cant focus");
                return false;
            }

            if (!string.IsNullOrEmpty(editBox.Text))
            {
                SendKeyCombination(Keys.A, Keys.ControlKey);
                SendKey(Keys.Delete);
                //Utility.SendBackgroundKey(WowProcess.MainWindowHandle, (char)Keys.End, false);
                //Utility.SendBackgroundString(WowProcess.MainWindowHandle, new string('\b', editBox.Text.Length * 2), false);
            }

            if (!await Utility.WaitUntilAsync(() => string.IsNullOrEmpty(editBox.Text), TimeSpan.FromMilliseconds(500)))
            {
                Console.WriteLine("cant clear text");
                return false;
            }

            SendString(text);
            if (!await Utility.WaitUntilAsync(() => editBox.Text == text, TimeSpan.FromMilliseconds(500)))
            {
                Console.WriteLine("text verification fail");
                return false;
            }
            await Task.Delay(100);
            return true;
        }

        public async Task<bool> FocusEditBoxAsync(string editBoxName)
        {
            var eb = FocusedWidget;
            if (eb.Name == editBoxName)
                return true;
            await CycleEditBoxesAsync(editBoxName);
            var focus = FocusedWidget;
            return focus.Name == editBoxName;
        }

        public async Task<UIObject> NextEditBoxAsync()
        {
            var curr = FocusedWidget;
            SendKey(Keys.Tab);
            var isNotTimeout = await Utility.WaitUntilAsync(() =>
                FocusedWidget != null && !FocusedWidget.Equals(curr), 50);
            if (curr == null)
                return FocusedWidget != null && isNotTimeout ? FocusedWidget : null;
            return !curr.Equals(FocusedWidget) && isNotTimeout ? FocusedWidget : null;
        }

        public async Task<HashSet<UIObject>> CycleEditBoxesAsync(string editBoxName, int timeoutMilliseconds = 1000, int maxTries = 10)
        {
            var currFocus = FocusedWidget;
            var res = new HashSet<UIObject>();
            var t = Stopwatch.StartNew();
            var nextFocus = await NextEditBoxAsync();
            if (currFocus != null)
            {
                res.Add(currFocus);
                if (currFocus.Name == editBoxName)
                    return res;
            }
            var isNotTimeout = t.ElapsedMilliseconds < timeoutMilliseconds;
            while (isNotTimeout
                && nextFocus != null
                && !res.Contains(nextFocus)
                && res.Count < maxTries)
            {
                res.Add(nextFocus);
                if (nextFocus.Name == editBoxName) break;
                isNotTimeout = t.ElapsedMilliseconds < timeoutMilliseconds;
                nextFocus = await NextEditBoxAsync();
            }
            return res;
        }

        public async Task<bool> IsLoginScreenAsync()
        {
            return await IsLoginScreenAsync(CancellationToken.None, new PauseToken());
        }

        public async Task<bool> IsLoginScreenAsync(CancellationToken cancel, PauseToken pause)
        {
            if (CurrentGlueState != GlueState.Disconnected
                || IsInGame
                || IsConnectingOrLoading)
                return false;

            if (!await WaitGlobalsInitAsync(cancel, pause))
            {
                Console.WriteLine("globals init timout");
                return false;
            }
            if (!await Utility.WaitUntilAsync(async () =>
            {
                ResetGlobals();
                var w = await GetWidgetAsync<EditBox>("AccountLoginAccountEdit");
                if (w != null)
                    return w.IsVisible && w.IsShown;
                return false;
            }, cancel, pause, 60000, 500))
            {
                Console.WriteLine("login screen init timeout");
                return false;
            }
            return true;
        }

        private static int CountFrameDescendants(Frame obj)
        {
            return 1 + obj.Children.OfType<Frame>().Sum(cf => CountFrameDescendants(cf));
        }

        public async Task<bool> WaitGlueParentInitAsync()
        {
            // wait GlueParent is not null
            await Utility.WaitUntilAsync(() => !IsConnectingOrLoading, 60000, 500);
            await Utility.WaitUntilAsync(async () => await GetWidgetAsync<Frame>("GlueParent") != null, 1000, 100);
            var glueParent = await GetWidgetAsync<Frame>("GlueParent");
            if (glueParent == null)
                return true;
            var cnt = 0;
            await Utility.WaitUntilAsync(() =>
            {
                var cnt1 = CountFrameDescendants(glueParent);
                var f = cnt == cnt1;
                cnt = cnt1;
                return f;
            }, 5000, 500);

            ResetGlobals();
            
            return true;
        }

        public async Task<bool> IsCharacterSelectionScreenAsync()
        {
            if (IsInGame || IsConnectingOrLoading)
                return false;
            var t = await GetWidgetAsync<FontString>("GlueDialogText", 10000);
            if (t == null)
                return false;
            return CurrentGlueState == GlueState.CharacterSelection
                && !t.IsVisible;
        }

        public async Task<bool> IsGlueDialogVisibleAsync()
        {
            if (IsInGame || IsConnectingOrLoading)
                return false;
            var t = await GetWidgetAsync<FontString>("GlueDialogText", 10000);
            if (t == null)
                return false;
            return t.IsVisible;
        }

        public void SendKey(params Keys[] keys)
        {
            foreach (var key in keys)
            {
                Utility.SendBackgroundKey(WowProcess.MainWindowHandle, (char)key, false);
            }
        }

        public void SendKeyCombination(Keys key, params Keys[] modifiers)
        {
            Utility.SendBackgroundKeyCombination(WowProcess.MainWindowHandle, (char)key,
                modifiers.Select(k => (char)k).ToArray());
        }

        public async Task<bool> ClickAtAsync(PointF position, bool restore = true)
        {
            return await Utility.LeftClickAtPosAsync(
                WowProcess.MainWindowHandle, (int) position.X, (int) position.Y, false, restore);
        }
        
        public async Task<bool> DoubleClickAtAsync(PointF position, bool restore = true)
        {
            return await Utility.LeftClickAtPosAsync(
                WowProcess.MainWindowHandle, (int)position.X, (int)position.Y, false, restore);
        }
        
        public void SendString(string str)
        {
            // no control chars
            if (Regex.IsMatch(str, "^[^\t\b\n\r]*$"))
            {
                Utility.PasteBackgroundString(WowProcess.MainWindowHandle, str);
            }
            else
            {
                // split string into sequences of non-control and control chars
                Regex.Matches(str, "([^\t\b\n\r]*)([\t\b\n\r]*)")
                .Cast<Match>()
                .Select(m => new
                    {
                        noncontrolChars = m.Groups[1].Value,
                        controlChars = m.Groups[2].Value
                    })
                .Where(m => !string.IsNullOrEmpty(m.noncontrolChars) || !string.IsNullOrEmpty(m.controlChars))
                .ToList()
                .ForEach(m =>
                {
                    Utility.PasteBackgroundString(WowProcess.MainWindowHandle, m.noncontrolChars);
                    Utility.SendBackgroundString(WowProcess.MainWindowHandle, m.controlChars, false);
                });
            }
        }

        public async Task<bool> SendChatAsync(string str)
        {
            if (!IsInGame)
                return false;

            //var chatFrame = await GetWidgetAddressAsync("ChatFrame")
            
            var chk = new Func<bool>(() => FocusedWidget != null
                    && Regex.IsMatch(FocusedWidget.Name, "^ChatFrame\\d+EditBox$"));
            if (!chk())
            {
                SendKey(Keys.Enter);
                if (!await Utility.WaitUntilAsync(chk, 100))
                {
                    // timeout, try close full screen frames by sending Escs
                    SendKey(Keys.Escape);
                    SendKey(Keys.Enter);
                    if (!await Utility.WaitUntilAsync(chk, 100))
                    {
                        Console.WriteLine("can't open chat edit box");
                        return false;
                    }
                }
            }
            if (!await TypeIntoEditBoxAsync(FocusedWidget.Name, str))
            {
                Console.WriteLine("could not type into chat box");
                return false;
            }
            SendKey(Keys.Enter);
            if (!await Utility.WaitUntilAsync(() => !chk(), 100))
            {
                Console.WriteLine("could not send chat message, chat editbox did not closed for some reason");
                return false;
            }
            await Task.Delay(100);
            return true;
        }

        public async Task<bool> LogoutAsync(WowCredential credential, CancellationToken cancel, PauseToken pause)
        {
            if (!IsInGame)
                return false;

            if (!await SendChatAsync("/logout"))
            {
                Console.WriteLine("could not send logout command");
                return false;
            }

            // transition to character select screen
            if (!await Utility.WaitUntilAsync(async () =>
            {
                ResetGlobals();
                return await IsCharacterSelectionScreenAsync();
            }, TimeSpan.FromSeconds(30), 100))
            {
                Console.WriteLine("unexpected screen, it should be character select screen");
                return false;
            }

            // TODO dirty hack, sometimes it accidentially hit esc when glue dialogue still active
            // so realm selection screen pops up, insted we should wait until it disappears
            await Task.Delay(2000, cancel);
            if (!await Utility.WaitUntilAsync(async () => !(await IsGlueDialogVisibleAsync()), cancel, pause, TimeSpan.FromMinutes(1), 100))
            {
                // TODO revisit message
                if (!cancel.IsCancellationRequested)
                    Console.WriteLine("connection timed out");
                return false;
            }

            var realm = await CurrentCharacterRealmAsync();
            var activeCharNames = (await GetWidgetsAsync<FontString>())
                .Where(w => w.IsVisible
                    && Regex.IsMatch(w.Name, "^CharSelectCharacterButton\\d+ButtonTextName")
                    // character name contains only word characters,
                    // colored text (like "text |...|r") means that character is inactive
                    && Regex.IsMatch(w.Text, "^\\w+$"))
                .Select(w => w.Text.Split(' ')[0]).ToList();

            if (!activeCharNames.Contains(credential.CharacterName) || realm != credential.Realm)
            {
                SendKey(Keys.Escape);
                if (!await Utility.WaitUntilAsync(async () => await IsLoginScreenAsync(), 60000, 500))
                {
                    Console.WriteLine("can not reach login screen");
                    return false;
                }
            }

            await WaitGlueParentInitAsync();

            return true;
        }

        public async Task<string> CurrentCharacterNameAsync(int timeoutMilliseconds = 5000)
        {
            if (!IsInGame)
                return null;
            var w = await GetWidgetAsync<FontString>("PlayerName", timeoutMilliseconds);
            return (w != null) ? w.Text : null;
        }

        public async Task<string> GetLuaResultAsync(string luaExpression, string outputVariableName = "outputVar", int timeoutMilliseconds = 5000)
        {
            if (!IsInGame)
                return null;

            var chatCommand = string.Format("/run _G[\'{0}\'] = tostring({1})", outputVariableName, luaExpression);
            await SendChatAsync(chatCommand);
            ResetGlobals();
            if (!await Utility.WaitUntilAsync(() => Globals.GetValue(outputVariableName) != null, 1000, 100))
            {
                Console.WriteLine("check \"{0}\" variable failed", outputVariableName);
                return null;
            }
            await Task.Delay(500);
            return Globals.GetValue(outputVariableName).String.Value;
        }

        public async Task<bool> IsRealmSelectScreenAsync()
        {
            if (CurrentGlueState == GlueState.ServerSelection)
            {
                var frame = await GetWidgetAsync<Frame>("RealmList");
                return frame != null && frame.IsVisible;
            }
            return false;
        }

        public async Task<string> CurrentCharacterRealmAsync(int timeoutMilliseconds = 5000)
        {
            if (IsInGame)
            {
                if (Globals == null)
                    return null;
                var v = Globals.GetValue("realm");
                if (v == null || v.Type != LuaType.String || string.IsNullOrEmpty(v.String.Value))
                    return null;
                return v.String.Value;
            }
            if (await IsCharacterSelectionScreenAsync())
            {
                var realmString = await GetWidgetAsync<FontString>("CharSelectRealmName");
                if (realmString == null)
                    return null;
                // string in form of "Xxxxxx (PvP)", so we take the part before '('
                return realmString.Text.Split('(')[0].TrimEnd();
            }
            return null;
        }

        public async Task<bool> IsCharacterCreationScreenAsync()
        {
            if (IsInGame || IsConnectingOrLoading)
                return false;
            var t = await GetWidgetAsync<Frame>("CharacterCreateFrame", 10000);
            if (t == null)
                return false;
            return CurrentGlueState == GlueState.CharacterCreation
                && t.IsVisible;
        }

        public async Task<bool> SelectRealmAsync(string realmName)
        {
            var currRealmName = await CurrentCharacterRealmAsync();

            if (currRealmName == realmName)
                return true;

            if (!await IsRealmSelectScreenAsync())
            {
                if (!await IsCharacterSelectionScreenAsync())
                {
                    Console.WriteLine("character select screen should be active when selecting realm");
                    return false;
                }
                var btn = await GetWidgetAsync<Button>("CharSelectChangeRealmButton");
                if (btn != null)
                {
                    await btn.ClickAsync();
                }
            }
            if (!await Utility.WaitUntilAsync(async () => await IsRealmSelectScreenAsync(), 10000, 100))
            {
                Console.WriteLine("could not open realm select screen");
                return false;
            }

            var scrollDown = await GetWidgetAsync<Button>("RealmListScrollFrameScrollBarScrollDownButton");
            var scrollUp = await GetWidgetAsync<Button>("RealmListScrollFrameScrollBarScrollUpButton");
            if (scrollDown == null || scrollUp == null)
            {
                Console.WriteLine("no scroll buttons");
                return false;
            }
            Button realmBtn = null;
            while ((realmBtn = (await GetWidgetsAsync<Button>())
                .FirstOrDefault(b => b.Text == realmName)) == null)
            {
                await scrollDown.ClickAsync();
            }
            await realmBtn.ClickAsync();
            await realmBtn.ClickAsync();
            if (!await Utility.WaitUntilAsync(async () => await IsCharacterSelectionScreenAsync(), 10000, 300))
            {
                Console.WriteLine("failed to detect character selection screen");
                return false;
            }
            ResetGlobals();
            await Task.Delay(1000);
            return true;
        }

        public async Task<WowCredential> GetIntoTheGameAsync(WowCredential credential)
        {
            return await GetIntoTheGameAsync(credential, new CancellationToken(), new PauseToken());
        }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="credential"></param>
        /// <returns></returns>
        public async Task<WowCredential> GetIntoTheGameAsync(WowCredential credential, CancellationToken cancel, PauseToken pause)
        {
            if (credential == null)
                throw new ArgumentException("credential == null");
            if (!credential.IsValid())
                throw new ArgumentException("credential.IsValid() == false");
            if (IsInGame)
            {
                var charName = await CurrentCharacterNameAsync();
                var realm = await CurrentCharacterRealmAsync();
                if (charName == null)
                {
                    Console.WriteLine("could not get character name ingame");
                    return null;
                }
                if (charName != credential.CharacterName || realm != credential.Realm)
                {
                    if (!await LogoutAsync(credential, cancel, pause))
                    {
                        Console.WriteLine("could not logout, to switch characters: {0} to {1}", charName, credential.CharacterName);
                        return null;
                    }
                }
            }
            if (await IsLoginScreenAsync())
            {
                if (cancel.IsCancellationRequested)
                {
                    return null;
                }

                var retry = 0;
                while ((await IsGlueDialogVisibleAsync() || await GetLoginResultAsync() != LoginResult.NoResult) && retry < 5)
                {
                    retry++;
                    SendKey(Keys.Escape);
                    await Task.Delay(500, cancel);
                }
                if (await IsGlueDialogVisibleAsync())
                {
                    Console.WriteLine("unhandled dialog prevents login");
                    return null;
                }

                await pause.WaitWhilePausedAsync();
                // TODO factor out string constants
                if (!await TypeIntoEditBoxAsync("AccountLoginAccountEdit", credential.Login)
                    || !await TypeIntoEditBoxAsync("AccountLoginPasswordEdit", credential.Password))
                {
                    Console.WriteLine("can't enter credentials");
                    return null;
                }
                SendKey(Keys.Enter);
                
                // handle auth token frame
                if (!await HandleAuthTokenAsync(credential))
                {
                    Console.WriteLine("failed to handle auth token");
                    return null;
                }

                if (!await Utility.WaitUntilAsync(async () => !(await IsGlueDialogVisibleAsync()), cancel, pause, TimeSpan.FromMinutes(1), 100))
                {
                    // TODO revisit message
                    if (!cancel.IsCancellationRequested)
                        Console.WriteLine("connection timed out");
                    return null;
                }
                if (await GetLoginResultAsync() != LoginResult.NoResult)
                {
                    Console.WriteLine("unsuccessful login");
                    return null;
                }
            }
            if (await IsCharacterCreationScreenAsync())
            {
                if (cancel.IsCancellationRequested)
                {
                    return null;
                }
                await pause.WaitWhilePausedAsync();
                SendKey(Keys.Escape);
                if (!await Utility.WaitUntilAsync(async () => !(await IsGlueDialogVisibleAsync()), cancel, pause, TimeSpan.FromSeconds(30), 100))
                {
                    // TODO revisit message
                    if (!cancel.IsCancellationRequested)
                        Console.WriteLine("connection timed out");
                    return null;
                }
            }
            if (await IsCharacterSelectionScreenAsync())
            {
                if (cancel.IsCancellationRequested)
                {
                    return null;
                }
                await pause.WaitWhilePausedAsync();
                var currRealmName = await CurrentCharacterRealmAsync();

                var spottedCharNames = (await GetWidgetsAsync<FontString>())
                    .Where(w => w.IsVisible
                        && Regex.IsMatch(w.Name, "^CharSelectCharacterButton\\d+ButtonTextName")
                        // character name contains only word characters,
                        // colored text (like "text |...|r") means that character is inactive
                        && Regex.IsMatch(w.Text, "^\\w+$"))
                    .Select(w => string.Format("{0}-{1}", w.Text.Split(' ')[0], currRealmName)).ToList();
                credential.Characters.UnionWith(spottedCharNames);

                if (currRealmName != credential.Realm)
                {
                    if (!await SelectRealmAsync(credential.Realm))
                    {
                        Console.WriteLine("could not select realm");
                        return null;
                    }
                }

                var activeCharNames = (await GetWidgetsAsync<FontString>())
                    .Where(w => w.IsVisible
                        && Regex.IsMatch(w.Name, "^CharSelectCharacterButton\\d+ButtonTextName")
                        // character name contains only word characters,
                        // colored text (like "text |...|r") means that character is inactive
                        && Regex.IsMatch(w.Text, "^\\w+$"))
                    .Select(w => w.Text.Split(' ')[0]).ToList();
                credential.Characters.UnionWith(activeCharNames.Select(s => string.Format("{0}-{1}", s, credential.Realm)));
                // TODO emit event account character names list was updated

                if (!activeCharNames.Contains(credential.CharacterName))
                {
                    Console.WriteLine("there's no character with specified name ({0}) or it is inactive", credential.CharacterName);
                    activeCharNames.ForEach(n => Console.WriteLine("\'{0}\'", n));
                    return null;
                }

                var selectedChar = await GetWidgetAsync<FontString>("CharSelectCharacterName");
                while (selectedChar.Text != credential.CharacterName)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        return null;
                    }
                    await pause.WaitWhilePausedAsync();
                    var str = selectedChar.Text;
                    SendKey(Keys.Down);
                    if (!await Utility.WaitUntilAsync(() => str != selectedChar.Text, cancel, pause, 500, 100))
                    {
                        if (!cancel.IsCancellationRequested)
                            Console.WriteLine("can't move character selection cursor");
                        return null;
                    }
                }

                // transition into the game screen
                SendKey(Keys.Enter);
                if (!await Utility.WaitUntilAsync(() => IsInGame, cancel, pause, TimeSpan.FromMinutes(2), 100))
                {
                    if (!cancel.IsCancellationRequested)
                        Console.WriteLine("get into the game timed out");
                    return null;
                }

                if (!await Utility.WaitUntilAsync(
                    async () =>
                    {
                        ResetGlobals();
                        return !string.IsNullOrEmpty(await CurrentCharacterNameAsync());
                    }, cancel, pause, TimeSpan.FromMinutes(1), 300))
                {
                    if (!cancel.IsCancellationRequested)
                        Console.WriteLine("character name is not visible ingame");
                    return null;
                }
            }

            if (IsInGame)
            {
                if (cancel.IsCancellationRequested)
                {
                    return null;
                }
                await pause.WaitWhilePausedAsync();
                // init realm variable for future use
                CurrentCharacterRealmCached = await GetLuaResultAsync("GetRealmName()", "realm");
                if (CurrentCharacterRealmCached == null)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
            await Task.Delay(1000, cancel);
            return credential;
        }

        public async Task<string> GetGlueDialogTitleAsync()
        {
            var widget = await GetWidgetAsync<FontString>("GlueDialogTitle");
            if (widget != null && widget.IsVisible)
                return widget.Text;
            return null;
        }

        public async Task<LoginResult> GetLoginResultAsync()
        {
            try
            {
                var titleString = await GetGlueDialogTitleAsync();
                if (titleString == null)
                    return LoginResult.NoResult;
                // get the error code from the dialog title
                int code;
                int.TryParse(Regex.Match(titleString, "\\d+").Value, out code);
                switch (code)
                {
                    case 2:
                        return LoginResult.ConnectionFail;
                    case 202:
                        return LoginResult.Banned;
                    case 203:
                        return LoginResult.Suspended;
                    case 206:
                        return LoginResult.Frozen;
                    case 104:
                        return LoginResult.IncorrectPassword;
                    case 204:
                        return LoginResult.LockedLicense;
                    case 141:
                    case 42003:
                        return LoginResult.SuspiciousLocked;
                }
            }
            catch (Exception)
            {
                return LoginResult.Unknown;
            }
            return LoginResult.Unknown;
        }

        public enum LoginResult
        {
            ConnectionFail,
            IncorrectPassword,
            Banned,
            Suspended,
            Frozen,
            SuspiciousLocked,
            LockedLicense,
            Unknown,
            NoResult
        }

        public async Task<bool> HandleAuthTokenAsync(WowCredential credential)
        {
            var serial = credential.AuthenticatorSerial;
            var code = credential.AuthenticatorRestoreCode;
            if (string.IsNullOrEmpty(serial) && string.IsNullOrEmpty(code))
                return true;

            var frame = await GetWidgetAsync<Frame>("TokenEnterDialogBackgroundEdit");

            if (!await Utility.WaitUntilAsync(() => frame.IsVisible && frame.IsShown, 30000, 100))
            {
                Console.WriteLine("auth token frame did not showed up");
                return false;
            }

            if (frame == null || !frame.IsVisible || !frame.IsShown)
                return true;


            var auth = new BattleNetAuthenticator();
            try
            {
                auth.Restore(serial, code);
            }
            catch (Exception ex)
            {
                Console.WriteLine("could not get auth token: {0}", ex);
                return false;
            }

            if (!await TypeIntoEditBoxAsync("TokenEnterDialogBackgroundEdit", auth.CurrentCode))
                return false;
            SendKey(Keys.Enter);
            return true;
            
        }

        public async Task<bool> CloseActiveFramesAsync()
        {
            await Task.Delay(100);
            return true;
        }

        public async Task<bool> ExitAsync()
        {
            if (IsInGame)
            {
                if (!await SendChatAsync("/exit"))
                {
                    Console.WriteLine("could not type /exit");
                    return false;
                }
            }
            else
            {
                if (await IsCharacterCreationScreenAsync())
                {
                    SendKey(Keys.Escape);
                    if (!await Utility.WaitUntilAsync(async () => await IsCharacterSelectionScreenAsync(), 10000, 100))
                    {
                        Console.WriteLine("wait IsCharacterSelectionScreenAsync == true timeout");
                        return false;
                    }
                }
                if (await IsCharacterSelectionScreenAsync())
                {
                    SendKey(Keys.Escape);
                    SendKey(Keys.Escape);
                }
            }
            if (!await Utility.WaitUntilAsync(() => WowProcess.HasExited, TimeSpan.FromMinutes(2), 100))
            {
                Console.WriteLine("process has not exited");
                return false;
            }
            return true;
        }

        public string CurrentCharacterRealmCached { get; private set; }

        public bool IsInGame
        {
            get
            {
                try
                {
                    var state = GameStateAddress.Deref<byte>();
                    var loadingScreenCount = LoadingScreenEnableCountAddress.Deref<int>();
                    return state == 1 && loadingScreenCount == 0;
                }
                catch
                {
                    return false;
                }
            }
        }
        
        public bool IsConnectingOrLoading
        {
            get
            {
                try
                {
                    return GameStateAddress.Deref<byte>(1) == 1;
                }
                catch
                {
                    return false;
                }
            }
        }

        // wow process lifetime exit reason
        public enum ExitReason
        {
            Timeout,
            ProcessExited
        }
    }

    public enum GlueState
    {
        None = -1,
        Disconnected = 0,
        Updater,
        CharacterSelection = 2,
        CharacterCreation = 3,
        ServerSelection = 6,
        Credits = 7,
        RegionalSelection = 8
    }

}
