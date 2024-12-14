using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicDimension
{
    public class Config
    {
        [JsonProperty("clientId")] 
        public required string ClientId { get; set; }

        [JsonProperty("clientSecret")]
        public required string ClientSecret { get; set; }

        [JsonProperty("spotifyDeviceName")]
        public required string SpotifyDeviceName { get; set; }

        public string TokenFileName { get; set; } = null!;
    }
}
