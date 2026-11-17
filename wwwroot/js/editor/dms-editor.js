(function () {
    "use strict";
    function initialize() {
        var shell = document.getElementById("dms-editor-shell");
        if (!shell || shell.dataset.editorStarted) return;
        shell.dataset.editorStarted='true';
        var factory = window.DmsEditorAdapters && window.DmsEditorAdapters[shell.dataset.provider];
        var settings=JSON.parse(document.getElementById("editor-settings").textContent);
        var status=document.getElementById("dms-editor-status"), warning=document.getElementById("dms-continue-editing"), retry=document.getElementById('dms-editor-retry'),dirty=false,lastBeat=0,idleTimer,timeout;
        function finish(){clearTimeout(timeout);if(window.DmsLoader)DmsLoader.stop('document-editor');}
        function failed(text){finish();status.textContent=text;if(retry)retry.hidden=false;}
        if(retry)retry.addEventListener('click',function(){window.location.reload();});
        if(!factory){failed('Editor adapter unavailable. Retry loading or check the configured editor.');return;}
        timeout=setTimeout(function(){failed("The editor did not load within 30 seconds. Retry loading or ask the administrator to check editor connectivity.");},30000);
        async function post(url,value,background){var controller=new AbortController(),deadline=setTimeout(function(){controller.abort();},10000);try{var response=await fetch(url,{method:"POST",credentials:"same-origin",signal:controller.signal,dmsBackground:!!background,headers:{"Content-Type":"application/json","RequestVerificationToken":document.querySelector('#dms-editor-csrf input').value},body:JSON.stringify(value||{})});if(!response.ok||response.redirected)throw new Error("Session expired or request refused. Save your work and sign in again.");return await response.json();}finally{clearTimeout(deadline);}}
        function activity(){clearTimeout(idleTimer);warning.hidden=true;idleTimer=setTimeout(function(){warning.hidden=false;},14*60*1000);document.dispatchEvent(new Event("dms:editor-activity"));if(Date.now()-lastBeat<30000)return;lastBeat=Date.now();post('/api/editor/heartbeat/'+settings.SessionId,null,true).catch(function(){/* A failed heartbeat must not obscure the live document. */});}
        var initialLocation=JSON.parse(document.getElementById('editor-location').textContent), captureSequence=0,pendingLocationCapture=null,sectionMapping=null;
        async function loadSectionOptions(form,page,preferred){
            var field=form.querySelector('.dms-section-select'),manual=form.querySelector('.dms-section-manual');if(!field||!manual||!page||!shell.dataset.sectionOptionsUrl)return;
            var prior=preferred||(!manual.disabled?manual.value:field.value);
            try{
                var result=await post(shell.dataset.sectionOptionsUrl,{page:page},true),values=Array.isArray(result.sections)?result.sections:[];
                if(preferred&&!values.some(function(x){return x===preferred;}))values.unshift(preferred);
                field.replaceChildren(new Option(values.length?'Select a section…':'No section found on this page',''));
                values.forEach(function(value){field.add(new Option(value,value));});
                field.hidden=values.length===0;field.disabled=values.length===0;
                manual.hidden=values.length>0;manual.disabled=values.length>0;
                field.name=values.length?'sectionReference':'sectionReferenceSelect';manual.name=values.length?'sectionReferenceManual':'sectionReference';
                if(prior&&values.includes(prior))field.value=prior;
                else if(result.selected&&values.includes(result.selected))field.value=result.selected;
                else if(!values.length)manual.value=prior||'';
            }catch(e){field.hidden=true;field.disabled=true;field.name='sectionReferenceSelect';manual.hidden=false;manual.disabled=false;manual.name='sectionReference';manual.value=prior||'';}
        }
        async function updateLocation(selected,force){
            var form=document.getElementById('dms-add-comment');
            if(!form||!selected||(!force&&form.contains(document.activeElement)))return;
            var locationChanged=(selected.page&&Number(form.elements.pageNo.value)!==selected.page)
                ||(selected.text&&form.elements.anchorText&&form.elements.anchorText.value!==selected.text);
            if(selected.page)form.elements.pageNo.value=selected.page;
            if(locationChanged){var select=form.querySelector('.dms-section-select'),manual=form.querySelector('.dms-section-manual');if(select)select.value='';if(manual)manual.value='';}
            if(selected.page)await loadSectionOptions(form,selected.page,locationChanged?'':form.elements.sectionReference.value);
            if(!form.elements.anchorType)return;
            form.elements.anchorType.value=selected.text?'QUOTE':selected.page?'PAGE':'';
            form.elements.anchorText.value=selected.text||'';
            if(locationChanged)form.elements.sectionReference.value='';
            if((selected.text||selected.page)&&shell.dataset.sectionUrl&&(locationChanged||!form.elements.sectionReference.value)){
                var key=String(selected.page||'')+'|'+(selected.text||'');
                if(sectionMapping&&sectionMapping.key===key&&sectionMapping.form===form)return sectionMapping.promise;
                var sequence=++captureSequence,mapping={key:key,form:form};
                mapping.promise=(async function(){try{var section=await post(shell.dataset.sectionUrl,{selection:selected.text,page:selected.page||null},true);
                    if(sequence===captureSequence&&form.isConnected)await loadSectionOptions(form,selected.page||Number(form.elements.pageNo.value),section.section||'');
                }catch(e){if(force)status.textContent='Automatic section mapping is unavailable. Enter the section manually.';}}());
                sectionMapping=mapping;
                try{await mapping.promise;}finally{if(sectionMapping===mapping)sectionMapping=null;}
            }
        }
        function captureLocation(){pendingLocationCapture=(async function(){var selected=await adapter.capture();await updateLocation(selected,true);return selected;}());return pendingLocationCapture;}
        async function navigateLocation(location){
            if(!location.text&&location.section&&shell.dataset.sectionUrl){
                try{var heading=await post(shell.dataset.sectionUrl,{section:location.section});if(heading.text)location.text=heading.text;}catch(e){status.textContent=e.message;}
            }
            return await adapter.navigate(location);
        }
        var host={ready:function(){finish();if(retry)retry.hidden=true;status.textContent='';
            if(initialLocation){var location=initialLocation;initialLocation=null;
                navigateLocation(location).then(function(found){if(!found)status.textContent='Location unavailable in this document. Use the editor search.';}).catch(function(e){status.textContent=e.message;});}
        },notice:failed,activity:activity,dirty:function(value){dirty=value;},post:post,
            location:function(value){updateLocation(value,false);}};
        var adapter;
        if(window.DmsLoader)DmsLoader.start('document-editor');
        try{adapter=factory(document.getElementById("dms-editor-frame"),document.getElementById("dms-editor-launch"),host,settings);
            Promise.resolve(adapter.initialize()).catch(function(e){failed(e.message);});
        }catch(e){failed('The editor could not start. Retry loading or check editor diagnostics.');return;}
        activity();warning.addEventListener('click',activity);
        ['pointerdown','keydown'].forEach(function(name){document.addEventListener(name,function(e){if(e.isTrusted)activity();});});
        window.addEventListener('beforeunload',function(e){if(dirty){e.preventDefault();e.returnValue='';}});
        document.getElementById('dms-location-close').addEventListener('click',function(){document.getElementById('dms-location-overlay').hidden=true;});
        document.getElementById("dms-toggle-comments")?.addEventListener("click", function () {
            var hidden = shell.classList.toggle("comments-hidden");
            this.setAttribute("aria-expanded", String(!hidden));
            this.textContent = hidden ? "Show comments" : "Hide comments";
        });
        shell.addEventListener('focusin',function(event){
            if(event.target.name==='commentText')captureLocation().catch(function(e){status.textContent=e.message;});
        });
        shell.addEventListener("click", async function (event) {
            var capture=event.target.closest('.dms-capture-selection');
            if(capture){await captureLocation();return;}
            var button = event.target.closest(".dms-comment-location");if(!button)return;
            var a={type:button.dataset.anchorType,value:button.dataset.anchorValue,text:button.dataset.anchorText,page:Number(button.dataset.page),section:button.dataset.section};
            if(settings.ReadOnly&&shell.dataset.editUrl){
                var editUrl=new URL(shell.dataset.editUrl,window.location.origin);
                if(editUrl.origin!==window.location.origin)return;
                editUrl.searchParams.set('commentId',button.dataset.commentId);
                window.location.assign(editUrl.href);return;
            }
            if(await navigateLocation(a))return;
            // Editors must never be sent to a read-only rendition to address a finding.
            if(!settings.ReadOnly){status.textContent='Location unavailable in the live document. Use the editor search; editing remains active.';return;}
            var page=Number.isSafeInteger(a.page)&&a.page>0&&!a.text&&!a.value;
            var url=new URL(page?shell.dataset.pdfUrl:shell.dataset.locationUrl,window.location.origin);if(url.origin!==window.location.origin)return;
            if(page)url.hash='page='+a.page;
            var overlay=document.getElementById('dms-location-overlay'),frame=document.getElementById('dms-location-frame'),message=document.getElementById('dms-location-status');
            overlay.hidden=false;message.textContent=page?'Saved PDF page; live editor remains open.':'Saved text location; live editor remains open.';
            frame.onload=function(){if(page)return;try{
             var doc=frame.contentDocument,root=doc.getElementById('saved-content');if(!root)return;
             if(a.type==='BOOKMARK'){var bookmark=doc.getElementById(a.value);if(bookmark){bookmark.scrollIntoView();return;}}
             var quote=a.text||a.section;if(!quote){message.textContent='This bookmark is unavailable in saved text. Use the editor search or a page location.';return;}
             var walker=doc.createTreeWalker(root,NodeFilter.SHOW_TEXT),nodes=[],text='',node;
             while((node=walker.nextNode())){nodes.push({node:node,start:text.length});text+=node.textContent;}
             var start=text.indexOf(quote);if(start<0||text.indexOf(quote,start+1)>=0){message.textContent='The saved quote is missing or occurs more than once. Locate it manually; no exact jump was made.';return;}
             var end=start+quote.length,first=nodes.find(x=>x.start+x.node.length>start),last=nodes.find(x=>x.start+x.node.length>=end),range=doc.createRange();
             range.setStart(first.node,start-first.start);range.setEnd(last.node,end-last.start);var selection=doc.getSelection();selection.removeAllRanges();selection.addRange(range);first.node.parentElement.scrollIntoView({block:'center'});
            }catch{message.textContent='Use the saved document viewer to locate the finding.';}};
            frame.src=url.href;
        });
        // Saving a DMS finding refreshes only the panel; the live editor is not reloaded.
        shell.addEventListener("submit", async function (event) {
            var form = event.target;
            if (!form.closest("#dms-editor-comments")) return;
            event.preventDefault();
            var status = document.getElementById("dms-comment-status");
            var buttons = form.querySelectorAll("button[type=submit]");
            buttons.forEach(function (button) { button.disabled = true; });
            try {
                if(form.id==='dms-add-comment'&&pendingLocationCapture)await pendingLocationCapture;
                var response = await fetch(form.action, { method: "POST", body: new FormData(form),
                    credentials: "same-origin", headers: { "X-Requested-With": "XMLHttpRequest" } });
                if (!response.ok) throw new Error("The comment could not be recorded. Check your session and try again.");
                var result = await response.json();
                status.textContent = result.message || (result.ok ? "Recorded." : "The comment was refused.");
                if (result.ok) {
                    var panel = await fetch(shell.dataset.commentsUrl, { credentials: "same-origin" });
                    if (!panel.ok || panel.redirected) throw new Error("Recorded, but comments could not be refreshed. Reopen the document after saving your edits.");
                    document.getElementById("dms-comment-content").innerHTML = await panel.text();
                    await captureLocation();
                }
            } catch (error) {
                status.textContent = error.message || "The comment could not be recorded.";
            } finally {
                buttons.forEach(function (button) { button.disabled = false; });
            }
        });
        window.addEventListener("pagehide", function (event) { if (!event.persisted) adapter.destroy(); }, { once: true });
    }
    // The workspace markup precedes this script. Launch now, before unrelated
    // layout scripts/styles can delay DOMContentLoaded and leave the iframe blank.
    initialize();
}());
