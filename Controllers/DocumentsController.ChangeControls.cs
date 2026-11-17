using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;
namespace DMS.Web.Controllers;
public partial class DocumentsController
{
 [HttpGet]
 public async Task<IActionResult> ChangeControls(string? q){
  if(!HasPermission(DocumentPermissions.Create)&&!HasPermission(DocumentPermissions.Edit))return Forbid();
  if(q?.Length>128)return BadRequest();
  var references=await _unitOfWork.ChangeControl.SearchAsync(q,"ACTIVE");
  return Json(references.Where(r=>r.status is "VERIFIED" or "UNVERIFIED")
   .OrderByDescending(r=>r.created_at).ThenByDescending(r=>r.id)
   .Select(r=>new{id=r.cc_number,text=r.cc_number}));
 }
 private async Task<bool> ValidChangeControl(string? number,string? existing=null)=>
  string.IsNullOrWhiteSpace(number)||number==existing||
  (await _unitOfWork.ChangeControl.SearchAsync(number,"ACTIVE")).Any(r=>r.cc_number==number);
}
