(function(){'use strict';
 var busy=new Set(),timer,screen=document.createElement('div');screen.id='dms-application-loader';screen.hidden=true;screen.setAttribute('role','status');screen.setAttribute('aria-live','polite');
 screen.innerHTML='<div class="dms-loading-box"><span class="spinner-border" aria-hidden="true"></span><span>Processing…</span></div>';
 document.body.appendChild(screen);
 function start(id){busy.add(id);if(!timer)timer=setTimeout(function(){timer=null;screen.hidden=!busy.size;},250);}
 function stop(id){busy.delete(id);if(!busy.size){clearTimeout(timer);timer=null;screen.hidden=true;}}
 window.DmsLoader={start:start,stop:stop};
 // A navigation can be canceled by the browser's unsaved-changes prompt.
 // Clear its indicator before the prompt; actual network requests keep their IDs.
 window.addEventListener('beforeunload',function(){stop('navigation');});
 var original=window.fetch;window.fetch=function(input,options){
  if(options&&options.dmsBackground)return original.call(this,input,options);
  var id={};start(id);try{return original.call(this,input,options).finally(function(){stop(id);});}catch(e){stop(id);throw e;}
 };
 var send=XMLHttpRequest.prototype.send;XMLHttpRequest.prototype.send=function(){var id={};start(id);this.addEventListener('loadend',function(){stop(id);},{once:true});try{return send.apply(this,arguments);}catch(e){stop(id);throw e;}};
 document.addEventListener('submit',function(e){var f=e.target;if(e.defaultPrevented||f.target&&f.target!=='_self'||!f.checkValidity())return;start('navigation');setTimeout(function(){stop('navigation');},15000);});
 document.addEventListener('click',function(e){var a=e.target.closest('a[href]');if(!a||e.defaultPrevented||e.ctrlKey||e.metaKey||e.shiftKey||a.download||a.target&&a.target!=='_self')return;var u=new URL(a.href,location.href);if(u.origin!==location.origin||u.pathname===location.pathname&&u.search===location.search)return;start('navigation');setTimeout(function(){stop('navigation');},15000);});
 window.addEventListener('pageshow',function(event){if(!event.persisted)return;busy.clear();screen.hidden=true;clearTimeout(timer);timer=null;});
})();
