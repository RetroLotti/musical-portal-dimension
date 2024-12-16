namespace MusicDimension
{
    using Newtonsoft.Json;

    public class Config
    {
        [JsonProperty("clientId")] 
        public string ClientId { get; set; } = null!;

        [JsonProperty("clientSecret")]
        public string ClientSecret { get; set; } = null!;

        [JsonProperty("spotifyDeviceName")]
        public string SpotifyDeviceName { get; set; } = null!;

        public string TokenFileName { get; set; } = null!;
    }
}
