using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MusicDimension
{
    public class Song
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public SourceTypes Type { get; set; }

        [JsonProperty("spotifyType")]
        public SpotifyTypes SpotifyType { get; set; }

        [JsonProperty("spotifyId")]
        public string SpotifyId { get; set; }
    }

    public enum SpotifyTypes
    {
        Album,
        Track,
        Playlist
    }

    public enum SourceTypes
    {
        Spotify,
        Local,
        Other
    }
}
