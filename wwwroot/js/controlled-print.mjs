import {getDocument} from '/lib/pdfjs/build/pdf.mjs';
import {renderPage,pdfOptions} from '/js/restricted-viewer.mjs';
const status=document.getElementById('print-status'),button=document.getElementById('print-once'),pages=document.getElementById('controlled-print-pages');
let dispatched=false;
document.addEventListener('keydown',event=>{if((event.ctrlKey||event.metaKey)&&['p','s'].includes(event.key.toLowerCase())){event.preventDefault();event.stopImmediatePropagation();}},true);
for(const type of ['copy','cut','dragstart','contextmenu'])document.addEventListener(type,event=>event.preventDefault());
window.addEventListener('afterprint',()=>document.body.classList.remove('dms-authorized-print'));
function printOnce(){if(dispatched)return;dispatched=true;button.hidden=true;status.textContent='This numbered copy has been issued. Select a physical printer and one copy. Cancellation requires a new authorised copy.';document.body.classList.add('dms-authorized-print');try{window.print();}finally{document.body.classList.remove('dms-authorized-print');}}
button.addEventListener('click',printOnce);
try{
 const data=document.getElementById('controlled-print-data'),bytes=Uint8Array.from(atob(JSON.parse(data.textContent)),c=>c.charCodeAt(0));data.remove();
 const pdf=await getDocument({...pdfOptions(),data:bytes}).promise;
 for(let number=1;number<=pdf.numPages;number++){const canvas=document.createElement('canvas');canvas.className='controlled-print-page';await renderPage(pdf,number,canvas,1500);pages.append(canvas);}
 await pdf.destroy();status.textContent='Ready to print one numbered copy.';button.hidden=false;
}catch{status.textContent='The issued copy could not be displayed. Contact QA for a new authorised copy; it cannot be issued again.';}
