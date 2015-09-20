using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Shared;

namespace HonorbuddyClient
{
    [Serializable]
    public class HonorbuddyKeyPool
    {
        internal class HonorbuddyKey : IDisposable
        {
            private readonly HonorbuddyKeyPool _pool;
            private readonly string _key;
            private bool _disposed;
            public HonorbuddyKey(HonorbuddyKeyPool pool, string key)
            {
                _pool = pool;
                _key = key;
                _disposed = false;
            }
            public void Dispose()
            {
                if (_disposed)
                    return;
                _pool.Free(_key);
                _disposed = true;
            }
            public override string ToString()
            {
                if (!_disposed)
                    return _key;
                throw new InvalidOperationException("key already disposed");
            }
        }

        public HonorbuddyKeyPool()
        {
            Keys = new List<string>();
        }
        public HonorbuddyKeyPool(IEnumerable<string> list)
        {
            Keys = new List<string>(list.Select(Utility.EncrptDpapi));
        }
        [XmlElement(ElementName = "Key")]
        public List<string> Keys { get; private set; }
        public bool IsAny { get { return Keys.Any(); }}
        public IDisposable Allocate()
        {
            string key;
            try
            {
                key = Keys.First();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("there's no free honorbuddy keys");
            }
            Keys.Remove(key);
            return new HonorbuddyKey(this, Utility.DecrptDpapi(key));
        }
        internal void Free(string key)
        {
            Keys.Add(Utility.EncrptDpapi(key));
        }
    }

}
