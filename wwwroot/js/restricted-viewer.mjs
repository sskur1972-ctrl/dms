import {getDocument,GlobalWorkerOptions} from '/lib/pdfjs/build/pdf.mjs';
GlobalWorkerOptions.workerSrc='/lib/pdfjs/build/pdf.worker.mjs';
export async function renderPage(pdf,number,canvas,width=900){
 const page=await pdf.getPage(number),natural=page.getViewport({scale:1});
 const viewport=page.getViewport({scale:Math.min(width/natural.width,2)});
 canvas.width=Math.ceil(viewport.width);canvas.height=Math.ceil(viewport.height);
 await page.render({canvasContext:canvas.getContext('2d'),viewport}).promise;
 return page;
}
export function pdfOptions(){return {isEvalSupported:false,enableXfa:false,cMapUrl:'/lib/pdfjs/cmaps/',cMapPacked:true,standardFontDataUrl:'/lib/pdfjs/standard_fonts/',wasmUrl:'/lib/pdfjs/wasm/'};}
for(const root of document.querySelectorAll('[data-restricted-pdf]')){
 const controls=document.createElement('div');controls.className='dms-pdf-controls';
 const status=document.createElement('span');status.setAttribute('role','status');status.textContent='Preparing document…';controls.append(status);root.append(controls);
 root.addEventListener('contextmenu',e=>e.preventDefault());
 for(const type of ['copy','cut','paste','dragstart'])root.addEventListener(type,e=>e.preventDefault());
 const pages=document.createElement('div');pages.className='dms-pdf-pages';root.append(pages);
 try{
  if(root.dataset.statusUrl){
   const end=Date.now()+90000;
   for(;;){const response=await fetch(root.dataset.statusUrl,{credentials:'same-origin',dmsBackground:true,signal:AbortSignal.timeout(10000)});
    if(!response.ok||response.redirected)throw Error('Document access expired. Sign in again.');
    const value=await response.json();if(value.ready)break;if(value.failed)throw Error(value.message);
    if(Date.now()>end)throw Error('Document preparation is taking longer than expected. Reopen it shortly or check the rendition queue.');
    await new Promise(resolve=>setTimeout(resolve,1000));
   }
  }
  const pdf=await getDocument({...pdfOptions(),url:root.dataset.pdfUrl,withCredentials:true}).promise;
  const input=document.createElement('input');input.type='number';input.min=1;input.max=pdf.numPages;input.value=1;input.setAttribute('aria-label','Document page');
  controls.prepend(input);status.textContent='of '+pdf.numPages+' pages';
  const holders=[];let serial=Promise.resolve(),currentPage=0;
  function changed(number){if(currentPage===number)return;currentPage=number;input.value=number;root.dispatchEvent(new CustomEvent('dms:viewer-location',{bubbles:true,detail:{page:number}}));}
  const observer=new IntersectionObserver(entries=>{
   for(const entry of entries){if(!entry.isIntersecting)continue;const holder=entry.target,number=Number(holder.dataset.page);
    if(holder.dataset.rendered)continue;holder.dataset.rendered='pending';
    serial=serial.then(async()=>{const canvas=document.createElement('canvas');canvas.setAttribute('aria-label','Document page '+number);await renderPage(pdf,number,canvas,holder.clientWidth||900);holder.replaceChildren(canvas);holder.style.minHeight='0';holder.dataset.rendered='true';}).catch(()=>{holder.textContent='This page could not be rendered. Reopen the document.';});
   }
  },{root:root.closest('.dms-restricted-pane'),rootMargin:'150px'});
  const visible=new Map(),locationObserver=new IntersectionObserver(entries=>{
   for(const entry of entries)visible.set(Number(entry.target.dataset.page),entry.isIntersecting?entry.intersectionRatio:0);
   const best=[...visible.entries()].sort((a,b)=>b[1]-a[1])[0];if(best&&best[1]>0)changed(best[0]);
  },{root:root.closest('.dms-restricted-pane'),threshold:[0,.1,.25,.5,.75,1]});
  for(let number=1;number<=pdf.numPages;number++){const holder=document.createElement('div');holder.className='dms-pdf-page';holder.dataset.page=number;holder.style.width='100%';holder.style.maxWidth='1200px';holder.style.minHeight='1100px';holders.push(holder);pages.append(holder);observer.observe(holder);locationObserver.observe(holder);holder.addEventListener('pointerdown',()=>changed(number));}
  function navigate(number){number=Number(number);if(Number.isInteger(number)&&number>=1&&number<=pdf.numPages){holders[number-1].scrollIntoView({block:'start'});changed(number);}}
  root.dmsNavigate=navigate;input.addEventListener('change',()=>navigate(input.value));
  root.addEventListener('dms:viewer-navigate',e=>navigate(e.detail.page));
  navigate(root.dataset.page||new URLSearchParams(location.hash.slice(1)).get('page')||1);
 }catch(error){status.textContent=error.message||'The document could not be loaded.';const retry=document.createElement('button');retry.textContent='Retry loading';retry.type='button';retry.onclick=()=>location.reload();controls.append(retry);}
}
document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&['p','s'].includes(e.key.toLowerCase())){e.preventDefault();e.stopImmediatePropagation();}},true);
