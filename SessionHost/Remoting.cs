using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fenix
{

    [DataContract]
    public class EventData
    {
        [DataMember]
        public string EventType { get; set; }
    }

    [ServiceContract(CallbackContract = typeof(IClientAPI),
        SessionMode = SessionMode.Required)]
    public interface IServiceAPI
    {
        [OperationContract]
        bool Register();
        [OperationContract]
        void NotifyEvent(EventData eventData);
    }
    public interface IClientAPI
    {
        [OperationContract]
        void StartBot(string botname, string profile);
        [OperationContract]
        void StopBot();
    }

    [ServiceBehavior(InstanceContextMode = InstanceContextMode.PerSession,
        ConcurrencyMode = ConcurrencyMode.Multiple, UseSynchronizationContext = false,
        IncludeExceptionDetailInFaults = true)]
    class ServiceAPI : MarshalByRefObject, IServiceAPI
    {
        public bool Register()
        {
            return true;
        }

        public void NotifyEvent(EventData eventData)
        {
        }

        IClientAPI Callback
        {
            get
            {
                return OperationContext.Current.GetCallbackChannel<IClientAPI>();
            }
        }
    }
}
