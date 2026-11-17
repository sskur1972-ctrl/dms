using Microsoft.Extensions.Options;
namespace DMS.Web.Services.DocumentEditor;
public abstract class CallbackEditorProvider(EditorSessionStore sessions,IOptions<EditorOptions> options):IDocumentEditorProvider {
 protected EditorOptions Options=>options.Value;
 public abstract string Name{get;}
 public EditorCapabilities Capabilities=>new(true,true,true,true,Name=="ONLYOFFICE"&&Options.OnlyOffice.AutomationApiEnabled);
 public Task<bool> IsEnabledAsync(CancellationToken ct=default)=>Task.FromResult(Options.For(Name).Enabled);
 public async Task<EditorOpenResult> OpenAsync(EditorOpenContext c,CancellationToken ct=default){
  var p=Options.For(Name);var editor=EditorUrls.Require(p.BaseUrl,Options.AllowHttpForDevelopment).ToString().TrimEnd('/');
  var host=EditorUrls.Require(Options.PublicBaseUrl,Options.AllowHttpForDevelopment).ToString().TrimEnd('/');
  if(Name=="ONLYOFFICE")EditorJwt.Sign(new{},p.JwtSecret);
  var s=await sessions.CreateAsync(c,Name,ct);
  try{
   var ext=Path.GetExtension(s.FileName).TrimStart('.').ToLowerInvariant();
   var url=host+"/api/editor/file/"+s.Id+"?token="+Uri.EscapeDataString(sessions.FileToken(s));
   object config;string script;
   if(Name=="ONLYOFFICE"){
    var dict=new Dictionary<string,object>{["documentType"]=ext is "xlsx" or "ods"?"cell":ext is "pptx" or "odp"?"slide":"word",["width"]="100%",["height"]="100%",
     ["document"]=new{fileType=ext,key=s.DocumentKey,title=s.FileName,url,permissions=new{edit=s.CanWrite,review=s.CanWrite,comment=s.CanWrite,download=false,print=false,copy=false}},
     ["editorConfig"]=new{mode=s.CanWrite?"edit":"view",lang="en",callbackUrl=host+"/api/editor/onlyoffice/callback/"+s.Id,user=new{id=s.UserId.ToString(),name="DMS user "+s.UserId},customization=new{autosave=true,forcesave=true,compactHeader=true,comments=true}}};
    dict["token"]=EditorJwt.Sign(dict,p.JwtSecret);config=dict;script=editor+"/web-apps/apps/api/documents/api.js";
   }else{
    config=new{fileAttrs=new{fileInfo=new{name=Path.GetFileNameWithoutExtension(s.FileName),ext,primary=s.DocumentKey},sourceUrl=url,canEdit=s.CanWrite,canDownload=false},user=new{primary=s.UserId.ToString(),name="DMS user "+s.UserId,canSave=s.CanWrite,customization=new{@public=new{common=new{openReviewChanges=c.Mode==EditorMode.Review,openAllComments=true,viewVersion=!s.CanWrite}}}},editorAttrs=new{editorWidth="100%",editorHeight="100%",editorType=ext is "xlsx" or "ods"?"spreadsheet":ext is "pptx" or "odp"?"presentation":"document",platform="desktop",viewLanguage="en",canComment=s.CanWrite,canReview=s.CanWrite}};
    script=editor+"/web/editors/spireapi/SpireCloudEditor.js";
   }
   return new(new EditorSession{SessionId=s.Id,ExpiresAt=s.ExpiresAt,Provider=Name,Transport="JAVASCRIPT",DocumentId=s.DocumentId,VersionId=s.VersionId,Title=s.FileName,DocumentNumber=s.DocumentId.ToString(),LaunchUrl=editor,ScriptUrl=script,ClientConfig=config,ReadOnly=!s.CanWrite,Mode=c.Mode,Capabilities=Capabilities},null);
  }catch{await sessions.AbandonAsync(s,CancellationToken.None);throw;}
 }
}
public sealed class OnlyOfficeEditorProvider(EditorSessionStore sessions,IOptions<EditorOptions> options):CallbackEditorProvider(sessions,options){public override string Name=>"ONLYOFFICE";}
public sealed class SpireEditorProvider(EditorSessionStore sessions,IOptions<EditorOptions> options):CallbackEditorProvider(sessions,options){public override string Name=>"SPIRE";}
