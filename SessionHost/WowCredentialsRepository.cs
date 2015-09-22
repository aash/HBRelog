using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using WowClient;

namespace Fenix
{
    [Serializable]
    public class WowCredentialsRepository
    {
        [XmlElement(ElementName = "Account")]
        public List<WowCredential> Credential { get; set; }
        public WowCredential this[string characterRealm]
        {
            get { return Credential.FirstOrDefault(c => c.Characters.Contains(characterRealm)); }
        }
    }
}
