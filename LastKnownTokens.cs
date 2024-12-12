using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MusicDimension
{
    public class LastKnownTokens
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }

        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; }

        [JsonProperty("expiresIn")]
        public int ExpiresIn { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        public bool IsExpired => this.CreatedAt.AddSeconds((double)this.ExpiresIn) <= DateTime.UtcNow;
    }
}
