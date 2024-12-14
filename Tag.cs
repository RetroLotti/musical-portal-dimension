using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
