(function(){'use strict';function initialize(){var status=document.getElementById('attachment-status'),settings=JSON.parse(document.getElementById('attachment-settings').textContent);
 var loading='attachment',timer=setTimeout(function(){if(window.DmsLoader)DmsLoader.stop(loading);status.textContent='Attachment did not load. Close this tab and retry, or ask the administrator to check the file and editor service.';},30000);
 if(window.DmsLoader)DmsLoader.start(loading);
 var adapter=window.DmsEditorAdapters.COLLABORA(document.getElementById('attachment-frame'),document.getElementById('attachment-launch'),
 {ready:function(){clearTimeout(timer);if(window.DmsLoader)DmsLoader.stop(loading);status.textContent='';},notice:function(text){clearTimeout(timer);if(window.DmsLoader)DmsLoader.stop(loading);status.textContent=text;},activity:function(){},dirty:function(){}},settings);
 adapter.initialize();window.addEventListener('pagehide',function(){adapter.destroy();},{once:true});
}initialize();}());
