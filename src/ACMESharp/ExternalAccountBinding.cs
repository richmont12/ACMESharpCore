using Newtonsoft.Json;

namespace ACMESharp
{
    public class ExternalAccountBinding {
        [JsonProperty("protected")]
        public string Protected { get; set; }
        [JsonProperty("payload")]
        public string Payload { get; set; }
        [JsonProperty("signature")]
        public string Signature { get; set; }
    }
}
