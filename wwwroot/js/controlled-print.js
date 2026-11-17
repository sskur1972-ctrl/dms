(function(){'use strict';var frame=document.getElementById('controlled-print-frame'),status=document.getElementById('print-status'),fallback=document.getElementById('print-fallback');
 var data=JSON.parse(document.getElementById('controlled-print-data').textContent),bytes=Uint8Array.from(atob(data),function(c){return c.charCodeAt(0);});
 document.getElementById('controlled-print-data').remove();var url=URL.createObjectURL(new Blob([bytes],{type:'application/pdf'})),opened=false;
 fallback.href=url+'#toolbar=0';
 frame.addEventListener('load',function(){if(opened)return;opened=true;fallback.hidden=false;setTimeout(function(){try{frame.contentWindow.focus();frame.contentWindow.print();status.textContent='Select a printer and one copy. If the dialog did not open, use the Ctrl+P link. This numbered copy has been issued.';}catch{status.textContent='Open this copy with the link, then use Ctrl+P to select a printer and one copy. This numbered copy has been issued.';}},750);},{once:true});
 frame.src=url+'#toolbar=0';window.addEventListener('pagehide',function(){URL.revokeObjectURL(url);},{once:true});
})();
