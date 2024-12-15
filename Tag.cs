using Newtonsoft.Json;

namespace MusicDimension
{
    public class Tag
    {
        [JsonProperty("uid")]
        public required string Uid { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("songs")]
        public List<Song> Songs { get; set; } = [];
    }
}
