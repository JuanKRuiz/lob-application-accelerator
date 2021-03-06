﻿using Newtonsoft.Json;
using System.ComponentModel;

namespace SharepointConsoleApp.Models
{
    [JsonObject("sharePoint")]
    public class SharePointResource
    {
        [JsonProperty("displayName")]
        [Description("The name to display in the address book for the group.")]
        public string DisplayName { get; set; }
    }
}
