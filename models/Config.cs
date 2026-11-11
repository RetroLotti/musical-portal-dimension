namespace MusicDimension.models
{
    using Newtonsoft.Json;

    public class Config
    {
        [JsonProperty("enable")]
        public bool Enable { get; set; } = true;

        [JsonProperty("clientId")]
        public string ClientId { get; set; } = null!;

        [JsonProperty("clientSecret")]
        public string ClientSecret { get; set; } = null!;

        [JsonProperty("spotifyDeviceName")]
        public string SpotifyDeviceName { get; set; } = null!;

        [JsonIgnore]
        public string TokenFileName { get; set; } = null!;
    }
}
