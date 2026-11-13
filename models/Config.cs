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

        [JsonProperty("receiver")]
        public ReceiverConfig Receiver { get; set; } = new();

        [JsonProperty("mediaMaxFileSizeMb")]
        public int MediaMaxFileSizeMb { get; set; } = 512;

        [JsonIgnore]
        public string TokenFileName { get; set; } = null!;
    }

    public class ReceiverConfig
    {
        [JsonIgnore]
        public string? SoloistApiKey { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("audioDevice")]
        public string AudioDevice { get; set; } = "plughw:CARD=Headphones,DEV=0";

        [JsonProperty("bitrate")]
        public int Bitrate { get; set; } = 160;
    }
}
