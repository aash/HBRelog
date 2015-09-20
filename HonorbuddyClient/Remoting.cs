using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HonorbuddyClient
{

    [DataContract]
    public class EventData
    {
        [DataMember]
        public string Name { get; set; }
        [DataMember]
        public string Data { get; set; }
    }

    public class HonorbuddybEventArgs : EventArgs
    {
        public string Name { get; set; }
        public string Data { get; set; }
    }

    [ServiceContract(CallbackContract = typeof(IHonorbuddyProxyServiceHelper),
        SessionMode = SessionMode.Required)]
    public interface IHonorbuddyProxyService
    {
        [OperationContract]
        bool Register();
        [OperationContract]
        void NotifyEvent(EventData x);

        event EventHandler<HonorbuddybEventArgs> Event;
    }
    
    public interface IHonorbuddyProxyServiceHelper
    {
        [OperationContract]
        bool StartBot(string botname, string profile);
        [OperationContract]
        bool StopBot();
    }

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single,
        ConcurrencyMode = ConcurrencyMode.Multiple, IncludeExceptionDetailInFaults = true)]
    public class HonorbuddyProxyService : IHonorbuddyProxyService
    {
        private bool _isClientRegistered;
        public bool IsHelperRegistered { get { return _isClientRegistered; } }
        public IHonorbuddyProxyServiceHelper Helper { get; set; }
        public bool Register()
        {
            Console.WriteLine("register called");
            _isClientRegistered = true;
            // cache callback channel, so we can call clients api
            Helper = Callback;
            return true;
        }

        public void NotifyEvent(EventData x)
        {
            Console.WriteLine("notifyevent called");
            if (Event == null)
                return;
            Event(this, new HonorbuddybEventArgs
            {
                Data = x.Data,
                Name = x.Name
            });
        }

        public event EventHandler<HonorbuddybEventArgs> Event;

        IHonorbuddyProxyServiceHelper Callback
        {
            get
            {
                return OperationContext.Current.GetCallbackChannel<IHonorbuddyProxyServiceHelper>();
            }
        }
    }
    
}
