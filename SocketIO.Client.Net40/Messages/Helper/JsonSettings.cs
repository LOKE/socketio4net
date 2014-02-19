namespace SocketIOClient.Messages.Helper
{
    using Newtonsoft.Json;

    public static class JsonSettings
    {
        public static JsonSerializerSettings Default
        {
            get
            {
                return defaultSettings;
            }

            set
            {
                defaultSettings = value;
            }
        }

        private static JsonSerializerSettings defaultSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };
    }
}
