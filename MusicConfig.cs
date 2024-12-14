using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MusicDimension
{
    public class MusicConfig
    {
        [JsonProperty("unusedTags")] public List<Tag> UnusedTags { get; set; } = [];

        [JsonProperty("registeredTags")] public List<Tag> RegisteredTags { get; set; } = [];
    }
}
