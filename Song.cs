namespace MusicDimension
{
    using Newtonsoft.Json;

    public class Song
    {
        [JsonProperty("name")] 
        public string Name { get; set; } = null!;

        [JsonProperty("type")]
        public SourceTypes Type { get; set; }

        [JsonProperty("spotifyType")]
        public SpotifyTypes SpotifyType { get; set; }

        [JsonProperty("spotifyId")]
        public string SpotifyId { get; set; } = null!;
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
