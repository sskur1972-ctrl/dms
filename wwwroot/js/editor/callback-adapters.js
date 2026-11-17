(function(){
 'use strict';
 var adapters=window.DmsEditorAdapters=window.DmsEditorAdapters||{};
 function load(url){return new Promise(function(resolve,reject){var script=document.createElement('script');script.src=url;script.onload=resolve;script.onerror=function(){reject(new Error('Editor script failed to load. Check browser URL and trusted TLS certificate.'));};document.head.appendChild(script);});}
 adapters.ONLYOFFICE=function(frame,form,host,s){
  var editor,connector;
  function command(code){return new Promise(function(resolve){if(!connector){resolve(null);return;}var timer=setTimeout(function(){resolve(null);},5000);try{connector.callCommand(new Function(code),function(value){clearTimeout(timer);resolve(value);});}catch{clearTimeout(timer);resolve(null);}});}
  return {
   initialize:async function(){await load(s.ScriptUrl);var c=s.ClientConfig;c.events={onDocumentReady:function(){if(s.Capabilities.CanNavigateToAnchor&&editor.createConnector)connector=editor.createConnector();host.ready();},onError:function(){host.notice('ONLYOFFICE reported an error. Check diagnostics and server logs.');},onDocumentStateChange:function(e){host.dirty(!!e.data);if(e.data)host.activity();}};editor=new window.DocsAPI.DocEditor('dms-vendor-editor',c);},
   navigate:async function(a){
    if(a.type==='BOOKMARK'&&a.value)return !!await command('var b=Api.GetDocument().GetBookmark('+JSON.stringify(a.value)+');if(!b)return false;b.GoTo();return true;');
    if(a.text)return !!await command('var r=Api.GetDocument().Search('+JSON.stringify(a.text)+');if(r.length!==1)return false;r[0].Select();return true;');
    if(Number.isSafeInteger(a.page)&&a.page>0)return !!await command('return Api.GetDocument().GoToPage('+(a.page-1)+');');
    return false;
   },
   capture:async function(){return await command('var r=Api.GetDocument().GetRangeBySelect();return r?{text:r.GetText(),page:Api.GetDocument().GetCurrentPage()+1}:null;');},
   destroy:function(){if(connector&&connector.disconnect)connector.disconnect();if(editor)editor.destroyEditor();}
  };
 };
 adapters.SPIRE=function(frame,form,host,s){
  var editor;
  return {initialize:async function(){await load(s.ScriptUrl);var c=s.ClientConfig;c.editorAttrs.events={onStart:function(){host.notice('Editor connected; loading document…');},onDocumentContentReady:host.ready,onDocumentStateChange:function(e){host.dirty(!!e.data);if(e.data)host.activity();},onError:function(){host.notice('Spire reported an error. Check diagnostics.');},onSave:async function(e){
   if(!e||!Array.isArray(e.data)||typeof e.data[1]!=='string'){host.notice('Unsupported Spire save contract; no file was committed. This adapter requires classic Cloud Office URL callbacks.');return;}
   try{var result=await host.post('/api/editor/spire/save',{sessionId:s.SessionId,url:e.data[1],saveId:window.crypto.randomUUID()});if(!result.ok)throw new Error(result.message);host.dirty(false);host.notice('Saved to DMS.');}catch(error){host.notice(error.message||'Spire save failed.');}
  }};editor=new window.SpireCloudEditor.OpenApi('dms-vendor-editor',c);},navigate:async function(){return false;},capture:async function(){return null;},destroy:function(){if(editor&&editor.destroyEditor)editor.destroyEditor();}};
 };
}());
