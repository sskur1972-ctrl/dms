(function(){'use strict';
 document.querySelectorAll('.dms-cc-picker').forEach(function(root){
  if(root.dataset.initialized)return;root.dataset.initialized='true';
  var select=root.querySelector('.dms-cc-number'),error=root.querySelector('.dms-cc-error'),sequence=0,timer;
  var picker=typeof Choices==='function'?new Choices(select,{searchEnabled:true,searchPlaceholderValue:'Search Change Control No.',shouldSort:false,allowHTML:false,itemSelectText:'',searchResultLimit:500}):null;
  async function load(query){var request=++sequence,url=new URL(root.dataset.url,location.origin),selected=select.value;
   url.searchParams.set('q',query||'');
   try{var response=await fetch(url,{credentials:'same-origin'});if(!response.ok||response.redirected)throw new Error('Change Controls could not be loaded.');
    var rows=await response.json();if(request!==sequence)return;
    var options=[{value:'',label:'Select Change Control No.',placeholder:true,selected:!selected}];
    if(selected&&!rows.some(x=>x.id===selected))options.push({value:selected,label:selected,selected:true});
    rows.forEach(row=>options.push({value:row.id,label:row.text,selected:row.id===selected}));
    if(picker)picker.setChoices(options,'value','label',true);else{select.replaceChildren();options.forEach(row=>select.add(new Option(row.label,row.value,false,row.selected)));}
    error.textContent='';
   }catch(e){if(request===sequence)error.textContent=e.message;}
  }
  select.addEventListener('search',function(event){clearTimeout(timer);timer=setTimeout(()=>load(event.detail.value),200);});load('');
 });
}());
