namespace DMS.Web.Services.DocumentEditor;
public sealed class EditorOptions {
 public bool Enabled {get;set;}=true;
 public string Provider {get;set;}="COLLABORA";
 public string PublicBaseUrl {get;set;}="";
 public bool AllowHttpForDevelopment {get;set;}
 public int SessionMinutes {get;set;}=120;
 public int MaxFileMb {get;set;}=100;
 public ProviderOptions Collabora {get;set;}=new();
 public ProviderOptions OnlyOffice {get;set;}=new();
 public ProviderOptions Spire {get;set;}=new();
 public ProviderOptions For(string name)=>name switch {"COLLABORA"=>Collabora,"ONLYOFFICE"=>OnlyOffice,"SPIRE"=>Spire,_=>throw new InvalidOperationException("Unknown provider")};
}
public sealed class ProviderOptions {
 public bool Enabled {get;set;}
 public string BaseUrl {get;set;}="";
 public string DiscoveryUrl {get;set;}="";
 public string JwtSecret {get;set;}="";
 public string JwtHeader {get;set;}="Authorization";
 public bool AutomationApiEnabled {get;set;}
 public string[] AllowedDownloadOrigins {get;set;}=[];
}
public static class EditorUrls {
 public static Uri Require(string value,bool allowHttp=false) {
  if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||!string.IsNullOrEmpty(u.UserInfo)||
    (u.Scheme!="https"&&!(allowHttp&&u.Scheme=="http"))) throw new InvalidOperationException("Configure a valid HTTPS editor URL (HTTP is permitted only in explicit Development mode).");
  return u;
 }
 public static string Origin(Uri u)=>u.GetLeftPart(UriPartial.Authority);
}
