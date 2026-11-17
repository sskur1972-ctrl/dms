(function(){'use strict';function initialize(){
 var shell=document.getElementById('dms-editor-shell');if(!shell)return;
 var settings=JSON.parse(document.getElementById('editor-settings').textContent),status=document.getElementById('dms-editor-status'),dirty=false,lastBeat=0;
 async function post(url,value){var response=await fetch(url,{method:'POST',credentials:'same-origin',dmsBackground:url.indexOf('/api/editor/heartbeat/')===0,headers:{'Content-Type':'application/json','RequestVerificationToken':document.querySelector('#dms-editor-csrf input').value},body:JSON.stringify(value||{})});if(!response.ok||response.redirected)throw new Error('Session expired or template access refused.');return await response.json();}
 var loading='template-editor';if(window.DmsLoader)DmsLoader.start(loading);
 function finish(){clearTimeout(timeout);if(window.DmsLoader)DmsLoader.stop(loading);}
 var timeout=setTimeout(function(){finish();status.textContent='Template has not reported ready. Check the configured editor and connectivity.';},30000);
 var host={ready:function(){finish();status.textContent='';},notice:function(text){finish();status.textContent=text;},dirty:function(value){dirty=value;},location:function(){},post:post,
  activity:function(){if(Date.now()-lastBeat<30000)return;lastBeat=Date.now();post('/api/editor/heartbeat/'+settings.SessionId).catch(e=>status.textContent=e.message);}};
 var factory=window.DmsEditorAdapters&&window.DmsEditorAdapters[settings.Provider];if(!factory){finish();status.textContent='Editor adapter unavailable.';return;}
 var adapter=factory(document.getElementById('dms-editor-frame'),document.getElementById('dms-editor-launch'),host,settings);
 Promise.resolve(adapter.initialize()).catch(function(e){finish();status.textContent=e.message;});
 host.activity();['pointerdown','keydown'].forEach(name=>document.addEventListener(name,e=>{if(e.isTrusted)host.activity();}));
 window.addEventListener('beforeunload',function(e){if(dirty){e.preventDefault();e.returnValue='';}});
 window.addEventListener('pagehide',function(e){if(!e.persisted)adapter.destroy();});
}initialize();}());
