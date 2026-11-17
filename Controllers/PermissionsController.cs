using Microsoft.AspNetCore.Mvc;
using PTPLCRYPTORENGINE;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model;

namespace ProNest.Web.Controllers
{
    public class PermissionsController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        string _permissionList = string.Empty;

        public PermissionsController(IUnitOfWork unitOfWork, IHttpContextAccessor httpContextAccessor)
        {
            _unitOfWork = unitOfWork;
            _httpContextAccessor = httpContextAccessor;
            _permissionList = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value;

        }

        public async Task<IActionResult> Index()
        {
            try
            {
                if (_permissionList != null)
                {

                    if (_permissionList.Contains("\"View Permission\""))

                    {
                        List<Permissions> permission = await _unitOfWork.Permission.GetPermissionListAsync();
                        if (permission != null)
                        {
                            return View(permission);
                        }
                        else
                        {
                            List<Permissions> permissions = new List<Permissions>();
                            return View(permissions);
                        }
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
            catch (Exception ex)
            {
                List<Permissions> permissions = new List<Permissions>();
                return View(permissions);
            }
        }
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            if (_permissionList != null)
            {

                if (_permissionList.Contains("\"Create Permission\""))

                {
                    ViewBag.module = await _unitOfWork.Permission.GetModuleListAsync();
                    return View();
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
        }
        [HttpPost]
        public async Task<ActionResult<int>> Create(Permissions permission_data)
        {
            try
            {
                int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                var result = await _unitOfWork.Permission.AddPermissionAsync(permission_data, user);
                if (result == "1")
                {
                    TempData["success"] = "Permission created Successfully.";
                    TempData["Valid"] = "1";
                    return RedirectToAction("Index");
                }
                else
                {
                    throw new Exception("some error occurred permission not creatd.");
                }
            }
            catch (Exception ex)
            {

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "Permissions");
            }
        }
        public async Task<IActionResult> Edit(int id)
        {
            if (_permissionList != null)
            {
                if (_permissionList.Contains("\"Edit Permission\""))

                {
                    var permission_details = await _unitOfWork.Permission.GetPermissionByIDAsync(id);
                    ViewBag.module = await _unitOfWork.Permission.GetModuleListAsync();
                    if (permission_details == null || permission_details.Count() == 0)
                    {
                        throw new Exception("Permission Data not Found");
                    }
                    return View(permission_details.FirstOrDefault());
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
        }
        [HttpPost]
        public async Task<ActionResult<int>> Edit(Permissions obj)
        {
            try
            {
                int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                var result = await _unitOfWork.Permission.UpdatePermissionAsync(obj, user);

                if (result == "1")
                {
                    TempData["success"] = "Permission updated Successfully.";
                    TempData["Valid"] = "1";
                    return RedirectToAction("Index");
                }
                else
                {
                    throw new Exception("Failed to update permission.");
                }
            }
            catch (Exception ex)
            {

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "Permissions");
            }
        }
        [HttpGet]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                if (_permissionList != null)
                {
                    if (_permissionList.Contains("\"Delete Permission\""))
                    {
                        var permission_details = await _unitOfWork.Permission.GetPermissionByIDAsync(id);
                        ViewBag.module = await _unitOfWork.Permission.GetModuleListAsync();
                        if (permission_details == null || permission_details.Count() == 0)
                        {
                            throw new Exception("Permission Data not Found");
                        }
                        return View(permission_details.FirstOrDefault());
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "Permissions");
            }
        }
        [HttpPost]
        public async Task<ActionResult<int>> DeletePost(Permissions obj)
        {
            try
            {

                int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                var result = await _unitOfWork.Permission.DeletePermission(obj.pk_permission, Convert.ToInt32(user));

                if (result == "1")
                {
                    var DeleteRolePermission = await _unitOfWork.Permission.DeleteRolePermission(obj.pk_permission);

                    TempData["success"] = "Permission deleted successfully.";
                    TempData["Valid"] = "1";
                }
                else
                {
                    throw new Exception("Some error occurred permissions not deleted");
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "Home", new { area = "" });
            }
        }
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            try
            {
                if (_permissionList != null)
                {
                    if (_permissionList.Contains("\"View Permission\""))
                    {
                        ViewBag.module = await _unitOfWork.Permission.GetModuleListAsync();
                        var permission_details = await _unitOfWork.Permission.GetPermissionByIDAsync(id);
                        if (permission_details == null || permission_details.Count() == 0)
                        {
                            throw new Exception("Permission Data not Found");
                        }
                        return View(permission_details.FirstOrDefault());
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "Permissions");
            }
        }
        [HttpPost]
        public async Task<IActionResult> IsPermissionExisting(string Permission)
        {
            try
            {
                var data = await _unitOfWork.Permission.GetPermissionAlreadyExisted(Permission);
                if (data.Count() != 0)
                {
                    return Json(true);
                }
                else
                {
                    return Json(false);
                }
            }
            catch (Exception ex)
            {
                return RedirectToAction("Index", "Permissions");
            }
        }
    }
}
