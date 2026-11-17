(function(){'use strict';const reader=document.querySelector('.dms-reader');if(!reader)return;
 for(const type of ['copy','cut','paste','dragstart','contextmenu'])reader.addEventListener(type,e=>e.preventDefault());
 document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&['p','s','c','x','v'].includes(e.key.toLowerCase())){e.preventDefault();e.stopImmediatePropagation();}},true);
})();
