using Newtonsoft.Json;

namespace MusicDimension
{
    public class MusicConfig
    {
        [JsonProperty("unusedTags")]
        public List<Tag> UnusedTags { get; set; } = [];

        [JsonProperty("registeredTags")] 
        public List<Tag> RegisteredTags { get; set; } = [];
    }
}
