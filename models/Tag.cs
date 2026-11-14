namespace MusicDimension.models
{
    using Newtonsoft.Json;

    public class Tag
    {
        [JsonProperty("uid")]
        public required string Uid { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("songs")]
        public List<Song> Songs { get; set; } = [];

        [JsonProperty("resumePlayback")]
        public bool ResumePlayback { get; set; }

        [JsonProperty("audioOutput")]
        public string AudioOutput { get; set; } = "headphones";
    }
}
