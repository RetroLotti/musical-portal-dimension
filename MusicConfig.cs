namespace MusicDimension
{
    using Newtonsoft.Json;

    public class MusicConfig
    {
        [JsonProperty("unusedTags")]
        public List<Tag> UnusedTags { get; set; } = [];

        [JsonProperty("registeredTags")] 
        public List<Tag> RegisteredTags { get; set; } = [];

        // TODO: this is not implemented yet
        [JsonProperty("shortTags")]
        public Dictionary<string, string> ShortTags { get; set; } = [];
    }
}
