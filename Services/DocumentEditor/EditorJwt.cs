using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DMS.Web.Services.DocumentEditor;
public static class EditorJwt {
 static string Encode(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
 static byte[] Decode(string value)=>Convert.FromBase64String(value.Replace('-','+').Replace('_','/')+new string('=',(4-value.Length%4)%4));
 static byte[] Key(string secret)=>Encoding.UTF8.GetByteCount(secret)>=32?Encoding.UTF8.GetBytes(secret):throw new InvalidOperationException("ONLYOFFICE JWT secret must contain at least 32 UTF-8 bytes.");
 public static string Sign(object payload,string secret){var data=Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))+"."+Encode(JsonSerializer.SerializeToUtf8Bytes(payload));return data+"."+Encode(HMACSHA256.HashData(Key(secret),Encoding.ASCII.GetBytes(data)));}
 public static JsonElement Verify(string token,string secret){
  try{
   if(token.Length>65536)throw new Exception();var p=token.Split('.');if(p.Length!=3)throw new Exception();
   using var h=JsonDocument.Parse(Decode(p[0]));if(h.RootElement.GetProperty("alg").GetString()!="HS256")throw new Exception();
   if(!CryptographicOperations.FixedTimeEquals(Decode(p[2]),HMACSHA256.HashData(Key(secret),Encoding.ASCII.GetBytes(p[0]+"."+p[1]))))throw new Exception();
   using var d=JsonDocument.Parse(Decode(p[1]));var root=d.RootElement;var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   if(root.TryGetProperty("exp",out var exp)&&exp.GetInt64()<=now)throw new Exception();
   if(root.TryGetProperty("nbf",out var nbf)&&nbf.GetInt64()>now)throw new Exception();
   return (root.TryGetProperty("payload",out var nested)?nested:root).Clone();
  }catch{throw new UnauthorizedAccessException("Invalid editor callback signature.");}
 }
}
