using Excise.Model.Account;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Security.Claims;
using DMS.Model.Account;

namespace Excise.Web.Services
{
    public class GenericIdentityConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(CustomPrincipal);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jsonObject = JObject.Load(reader);

            // Extract the necessary properties from the JSON
            string name = jsonObject["Identity"]?["Name"]?.Value<string>();
            JArray claimsArray = jsonObject["permissions"] as JArray;
            // Create a new ClaimsIdentity with the extracted properties
            ClaimsIdentity identity = new ClaimsIdentity();
            if (claimsArray != null)
            {
                foreach (JValue claimObject in claimsArray)
                {
                    string claimType = (string)claimObject.Value;
                    string claimValue = "True";
                    identity.AddClaim(new Claim(claimType, claimValue));

                }
            }

            // Create a new CustomPrincipal with the created ClaimsIdentity

            CustomPrincipal principal = new CustomPrincipal(identity);
            principal.ID = jsonObject["ID"].Value<int>();
            principal.Username = jsonObject["Username"].Value<string>();
            principal.Roles_Name = jsonObject["Roles_Name"].Value<string>();
            principal.roles = jsonObject["roles"].Value<Int32>();
            List<string> permissions = claimsArray.ToObject<List<string>>();
            principal.permissions = permissions;
            // Populate other properties of CustomPrincipal if needed
            // ...

            return principal;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
}

