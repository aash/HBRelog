using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shared;

namespace HonorbuddyClient
{

    public interface IHonorbuddyProxy
    {
        bool StartBot(string botName, string profilePath);
        bool StopBot();

        event EventHandler<HonorbuddybEventArgs> Event;
    }


    /// <summary>
    /// HonorbuddyProxy (together with HonorbuddyClient.HelperPlugin) provide a way to
    /// communicate with Honorbuddy.exe process. The communication protocol is duplex so
    /// you can call a number of operations in a Honorbuddy.exe instance and handle events
    /// occuring in it.
    /// </summary>
    public class HonorbuddyProxy : IHonorbuddyProxy, IDisposable
    {
        private readonly Process _honorbuddyProcess;
        private readonly ServiceHost _serviceHost;
        private readonly HonorbuddyProxyService _proxy;
        private readonly IDisposable _hbKey;

        public bool IsDisposed { get; private set; }

        private static string GetEndPointName()
        {
            return "HonorbuddyProxyService";
        }

        public static async Task<HonorbuddyProxy> Create(
            string hbExePath, IDisposable hbKey, int wowPID, string combatRoutine, EventHandler<HonorbuddybEventArgs> onHbEventHandler = null)
        {
            if (hbKey == null)
                throw new ArgumentException("hbKey is null");

            HonorbuddyProxy hbProxy = null;
            Process process = null;
            ServiceHost host = null;

            // TODO ensure helper plugin is enabled via inspecting settings

            try
            {
                // create process
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    Arguments = string.Join(" ",
                        "/noupdate",
                        string.Format("/pid=\"{0}\"", wowPID),
                        string.Format("/hbkey=\"{0}\"", hbKey),
                        string.Format("/customclass=\"{0}\"", combatRoutine)
                        ),
                    FileName = hbExePath,
                };
                process = Process.Start(psi);
                if (process == null)
                {
                    hbKey.Dispose();
                    return null;
                }

                process.EnableRaisingEvents = true;

                if (!await Utility.WaitUntilAsync(() => process.Id != 0, 10000, 100))
                {
                    Console.WriteLine("honorbudd process create timeout");
                    return null;
                }

                // create service
                var uri = new Uri(string.Format("net.tcp://localhost:{0}/", process.Id));
                var instance = new HonorbuddyProxyService();
                // TODO: I cache the callback channel so timeout should be set
                // callback channel can be disconnected as of timeout
                host = new ServiceHost(instance, uri);
                hbProxy = new HonorbuddyProxy(process, host, hbKey);
                hbProxy.Event += onHbEventHandler;
                var endpoint = host.AddServiceEndpoint(typeof (IHonorbuddyProxyService),
                    new NetTcpBinding(), GetEndPointName());
                Console.WriteLine("listening on: {0}", endpoint.ListenUri);
                host.Open();

                // TODO monitor logs for different startup problems
                // invalid key, max sessions, fatal runtime errors

                // TODO wait until client connects

                if (!await Utility.WaitUntilAsync(() => instance.IsHelperRegistered, TimeSpan.FromMinutes(1), 100))
                {
                    Console.WriteLine("honorbuddy helper register timeout, (probably hbreloghelper disabled)");
                    process.CloseMainWindow();
                    if (!await Utility.WaitUntilAsync(() => process.HasExited, 2000, 100))
                    {
                        process.Kill();
                    }
                    host.Close();
                    return null;
                }
            }
            catch (Win32Exception e)
            {
                Console.WriteLine("can not start honorbuddy.exe process: {0}", e);
            }
            catch (Exception e)
            {
                Console.WriteLine("can not start honorbuddy.exe process: {0}", e);
            }
            finally
            {
                if (process == null || host == null || hbProxy == null)
                {
                    hbKey.Dispose();
                    if (host != null)
                        host.Close();
                    if (process != null)
                    {
                        process.CloseMainWindow();
                        var t = Utility.WaitUntilAsync(() => process.HasExited, 5000, 100);
                        t.Wait();
                        if (!t.Result && process != null && !process.HasExited)
                            process.Kill();
                    }
                }
            }
            return hbProxy;
        }

        public HonorbuddyProxy(Process honorbuddyProcess, ServiceHost serviceHost, IDisposable hbKey)
        {
            _honorbuddyProcess = honorbuddyProcess;
            _serviceHost = serviceHost;
            _proxy = serviceHost.SingletonInstance as HonorbuddyProxyService;
            _hbKey = hbKey;
            _honorbuddyProcess.Exited += (sender, args) =>
            {
                Console.WriteLine("process exited, disposing");
                Dispose();
            };
            IsDisposed = false;
        }

        public bool StartBot(string botName, string profilePath)
        {
            try
            {
                return _proxy.Helper.StartBot(botName, profilePath);
            }
            catch (Exception)
            {
                Dispose();
            }
            return false;
        }

        public bool StopBot()
        {
            try
            {
                return _proxy.Helper.StopBot();
            }
            catch (Exception)
            {
                Dispose();
            }
            return false;
        }

        public event EventHandler<HonorbuddybEventArgs> Event
        {
            add { _proxy.Event += value; }
            remove { _proxy.Event -= value; }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            if (_hbKey != null)
                _hbKey.Dispose();
            if (_honorbuddyProcess != null && !_honorbuddyProcess.HasExited)
            {
                _honorbuddyProcess.CloseMainWindow();
                // wait HasExited state, and kill if timeout
                var t = Utility.WaitUntilAsync(() => _honorbuddyProcess.HasExited, 2000, 100);
                t.Wait();
                if (!t.Result)
                {
                    if (_honorbuddyProcess != null && !_honorbuddyProcess.HasExited)
                    _honorbuddyProcess.Kill();
                }
            }
            if (_serviceHost != null
                && _serviceHost.State != CommunicationState.Faulted)
                _serviceHost.Close();
            IsDisposed = true;
            if (Disposed != null)
                Disposed(this, EventArgs.Empty);
        }

        public event EventHandler Disposed;
    }
}
