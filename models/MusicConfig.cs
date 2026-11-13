namespace MusicDimension.models
{
    using Newtonsoft.Json;

    public class MusicConfig
    {
        [JsonProperty("validSourceTypes")]
        public List<string> ValidSourceTypes { get; set; } = [];

        [JsonProperty("validSpotifyTypes")]
        public List<string> ValidSpotifyTypes { get; set; } = [];

        [JsonProperty("validControlTypes")]
        public List<string> ValidControlTypes { get; set; } = [];

        [JsonProperty("unusedTags")]
        public Dictionary<string, string> UnusedTags { get; set; } = [];

        [JsonProperty("registeredTags")]
        public List<Tag> RegisteredTags { get; set; } = [];

        // TODO: this is not implemented yet
        [JsonProperty("shortTags")]
        public Dictionary<string, string> ShortTags { get; set; } = [];

        [JsonProperty("controlTags")]
        public Dictionary<string, string> ControlTags { get; set; } = [];
    }
}
