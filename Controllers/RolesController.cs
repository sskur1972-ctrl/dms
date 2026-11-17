using Microsoft.AspNetCore.Mvc;
using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Account;
using DMS.Model;
using System.Threading.Tasks;

namespace DMS.Web.Controllers
{
    public class RolesController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ApplicationDbContext _dbContext;
        string _permissionList = string.Empty;
        public AuditTrailController auditTrailController;
        private IWebHostEnvironment _hostEnvironment;
        public RolesController(IUnitOfWork unitOfWork, IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext, IWebHostEnvironment hostEnvironment)
        {
            _unitOfWork = unitOfWork;
            _httpContextAccessor = httpContextAccessor;
            _dbContext = dbContext;
            _permissionList = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value;

            _hostEnvironment = hostEnvironment;

            auditTrailController = new AuditTrailController(_unitOfWork, _hostEnvironment, _httpContextAccessor, _dbContext);

        }

        public async Task<ActionResult> Index()
        {
            try
            {
                if (_permissionList != null)
                {
                    if (_permissionList.Contains("\"View Role\""))
                    {
                        var role = await _unitOfWork.Role.GetAllRoleListAsync();
                        List<Roles> objrole = new List<Roles>();

                        string? Sucess_message ="View Roles Index";
                        string? Activity_title = "Roles Index";
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        return View(role);
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = LangResource.Resource.viewroleslist;
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                List<Roles> objrole = new List<Roles>();
                return View(objrole);
            }
        }
        public async Task<ActionResult> Create()
        {
            try
            {
                if (_permissionList != null)
                {
                    if (_permissionList.Contains("\"Create Role\""))
                    {
                        RolesData rvm = new RolesData();
                        var permissionData = await _unitOfWork.Permission.GetPermissionListAsync();
                        rvm.objPermissions = permissionData;

                        string? Sucess_message = "Data Fetch For Create Role";
                        string? Activity_title = LangResource.Resource.RolesCreation;
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        return View(rvm);

                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");

            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = LangResource.Resource.RolesCreation;
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index");

            }
        }
        [HttpPost]
        public async Task<ActionResult<int>> Create(RolesData objroles)
        {
            
                try
                {
                    Roles new_role = new Roles();
                    new_role.role_name = objroles.Roles_Name;
                    new_role.IsActive = true;
                    int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                    var result = await _unitOfWork.Role.AddRoleAsync(new_role, user);
                    if (result > 0)
                    {
                        var role = await _unitOfWork.Role.getRoleNameByName(new_role.role_name);
                        foreach (var item in objroles.SelectedPermissionIds)
                        {
                            Role_permission new_role_permission = new Role_permission();
                            new_role_permission.role_id = role.FirstOrDefault().RoleId;
                            new_role_permission.permission_id = item;

                            var pid = await _unitOfWork.RolePermission.AddPermissionAsync(new_role_permission);
                            if (pid == 0)
                            {
                                throw new Exception("Failed to add permissions for the role");
                            }
                        }

                        string? Sucess_message = LangResource.Resource.Roles + objroles.Roles_Name + LangResource.Resource.creation;
                        string? Activity_title = LangResource.Resource.RolesCreation;
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                       

                        TempData["success"] = "Role created Successfully.";
                        TempData["Valid"] = "1";
                        return RedirectToAction("Index");
                    }
                    else
                    {
                        throw new Exception("some error occurred role not created.");
                    }
                }
                catch (Exception ex)
                {
                 
                    string? Sucess_message = ex.StackTrace.ToString();
                    string? Activity_title = LangResource.Resource.RolesCreation;
                    await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                   
                    TempData["success"] = ex.Message;
                    TempData["Valid"] = "0";
                    return RedirectToAction("Index");
                }
           
        }
        [HttpGet]

        public async Task<IActionResult> Edit(int id)
        {
            try
            {
                // Check permission
                if (_permissionList == null ||
                    !_permissionList.Contains("\"Edit Role\""))
                {
                    return RedirectToAction(
                        "PermissionErrorPage",
                        "AuthErrorPage"
                    );
                }

                // Get role details
                var role = await _unitOfWork.Role.getRoleName(id);

                if (role == null || !role.Any())
                {
                    throw new Exception("Roles Data not Found");
                }

                var selectedRole = role.FirstOrDefault();

                if (selectedRole == null)
                {
                    throw new Exception("Roles Data not Found");
                }

                // Create ViewModel
                RolesData obj_roleviewmodel = new RolesData();

                obj_roleviewmodel.ID = id;
                obj_roleviewmodel.Roles_Name = selectedRole.role_name;

                // Get all permissions
                List<Permissions> totalPermissions =
                    await _unitOfWork.Permission.GetPermissionListAsync();

                if (totalPermissions == null)
                {
                    totalPermissions = new List<Permissions>();
                }

                // Get permissions assigned to this role
                var rolePermissions =
                    await _unitOfWork.RolePermission
                        .getPermissions(selectedRole.RoleId);

                // Get only permission IDs assigned to the role
                var activePermissionIds = rolePermissions?
                    .Select(x => x.permission_id)
                    .ToHashSet()
                    ?? new HashSet<int>();

                // Mark assigned permissions as checked
                foreach (var permission in totalPermissions)
                {
                    permission.IsChecked =
                        activePermissionIds.Contains(permission.pk_permission);
                }

                // Assign permissions to ViewModel
                obj_roleviewmodel.objPermissions = totalPermissions;

                // Audit trail
                string? Sucess_message =
                    LangResource.Resource.UpdateRolesDetails;

                string? Activity_title =
                    LangResource.Resource.UpdateRoles;

                await auditTrailController.AddSucessAuditTrail(
                    Sucess_message,
                    Activity_title
                );

                return View(obj_roleviewmodel);
            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace?.ToString();

                string? Activity_title =
                    LangResource.Resource.UpdateRoles;

                await auditTrailController.AddErrorAuditTrail(
                    Sucess_message,
                    Activity_title
                );

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";

                return RedirectToAction("Index");
            }
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(RolesData rvm)
        {

                try
                {
                    var role = await _unitOfWork.Role.getRoleName(rvm.ID);

                    if (role == null)
                    {
                        throw new Exception("Roles Data not Found");
                    }
                    else
                    {
                        int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                        role.FirstOrDefault().role_name = rvm.Roles_Name;

                        var roledata = await _unitOfWork.Role.getRoleName(rvm.ID);
                        var update_role = await _unitOfWork.Role.UpdateRoleAsync(role.FirstOrDefault(), user);
                        if (update_role == 0)
                        {
                            throw new Exception("Failed to update role");
                        }
                        //delete the existing permission
                        var rolePermissionData = await _unitOfWork.RolePermission.getRolePermissions(role.FirstOrDefault().RoleId);
                        if (rolePermissionData.Count() != 0)
                        {
                            int result = await _unitOfWork.RolePermission.RemovePermissions(role.FirstOrDefault().RoleId);
                            if (result <= 0)
                            { throw new Exception("Failed to update role"); }
                        }

                        if (rvm.SelectedPermissionIds != null)
                        {

                            //add the new permissions
                            foreach (var item in rvm.SelectedPermissionIds)
                            {
                                Role_permission new_role_permission = new Role_permission();
                                new_role_permission.role_id = role.FirstOrDefault().RoleId;
                                new_role_permission.permission_id = item;
                                //new_role_permission.r = "OK";

                                int data = await _unitOfWork.RolePermission.AddPermissionAsync(new_role_permission);

                                if (data == 0)
                                {
                                    throw new Exception("Failed to update permissions for the role");
                                }

                            }
                        }

                        string? Sucess_message = LangResource.Resource.Roles + rvm.Roles_Name + LangResource.Resource.Updation;
                        string? Activity_title = LangResource.Resource.UpdateRoles;
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        TempData["success"] = "Role updated Successfully.";
                        TempData["Valid"] = "1";
                        return RedirectToAction("Index");

                    }
                }
                catch (Exception ex)
                {
                   
                    string? Sucess_message = ex.StackTrace.ToString();
                    string? Activity_title = LangResource.Resource.UpdateRoles;
                    await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                    
                    TempData["success"] = ex.Message;
                    TempData["Valid"] = "0";
                    return RedirectToAction("Index");
                }
            
        }
        public async Task<ActionResult> Details(int? id)
        {
            try
            {
                if (_permissionList != null)
                {

                    if (_permissionList.Contains("\"View Role\""))

                    {

                        RolesData obj_roleviewmodel = new RolesData();
                        if (id == null)
                        {
                            throw new Exception("Roles Data not Found");
                        }
                        else
                        {
                            var role = await _unitOfWork.Role.getRoleName(id);
                            if (role == null)
                            {
                                throw new Exception("Roles Data not Found");
                            }
                            obj_roleviewmodel.ID = id;
                            obj_roleviewmodel.Roles_Name = role.FirstOrDefault().role_name;

                            List<Permissions> totalPermissions = await _unitOfWork.Permission.GetPermissionListAsync();
                            List<Role_permission> Activeprm = new List<Role_permission>();

                            var rolepermission = await _unitOfWork.RolePermission.getPermissions(role.FirstOrDefault().RoleId);

                            if (rolepermission != null)
                            {
                                foreach (var item in rolepermission)
                                {
                                    var pm = await _unitOfWork.Permission.GetPermissionByIDAsync(item.permission_id);

                                    if (pm != null)
                                    {
                                        totalPermissions.Remove(pm.FirstOrDefault());
                                        pm.FirstOrDefault().IsChecked = true;

                                        totalPermissions.Insert(0, pm.FirstOrDefault());
                                    }
                                }
                                obj_roleviewmodel.objPermissions = totalPermissions.ToList();
                                return View(obj_roleviewmodel);
                            }

                            string? Sucess_message = "Data Fetch for View Roles";
                            string? Activity_title = LangResource.Resource.RolesDetails;
                            await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                            return View(obj_roleviewmodel);
                        }
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = LangResource.Resource.RolesDetails;
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";

                return RedirectToAction("Index");
            }

        }
        public async Task<ActionResult> Delete(int? id)
        {
            try
            {

                if (_permissionList != null)
                {

                    if (_permissionList.Contains("\"Delete Role\""))

                    {
                        RolesData obj_roleviewmodel = new RolesData();
                        if (id == null)
                        {
                            throw new Exception("Roles Data not Found");
                        }
                        else
                        {
                            var role = await _unitOfWork.Role.getRoleName(id);
                            if (role == null)
                            {
                                throw new Exception("Roles Data not Found");
                            }

                            obj_roleviewmodel.ID = id;
                            obj_roleviewmodel.Roles_Name = role.FirstOrDefault().role_name;

                            List<Permissions> totalPermissions = await _unitOfWork.Permission.GetPermissionListAsync();

                            List<Role_permission> Activeprm = new List<Role_permission>();

                            var rolepermission = await _unitOfWork.RolePermission.getPermissions(role.FirstOrDefault().RoleId);

                            if (rolepermission != null)
                            {
                                //get active permissions
                                foreach (var item in rolepermission)
                                {
                                    var pm = await _unitOfWork.Permission.GetPermissionByIDAsync(item.permission_id);

                                    if (pm != null)
                                    {
                                        totalPermissions.Remove(pm.FirstOrDefault());
                                        pm.FirstOrDefault().IsChecked = true;

                                        totalPermissions.Insert(0, pm.FirstOrDefault());
                                    }
                                }
                                obj_roleviewmodel.objPermissions = totalPermissions.ToList();
                                return View(obj_roleviewmodel);
                            }

                            string? Sucess_message = "Data fetch for delete role";
                            string? Activity_title = LangResource.Resource.RolesDelete;
                            await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                            return View(obj_roleviewmodel);
                        }
                    }
                }


                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");


            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = LangResource.Resource.RolesDelete;
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";

                return RedirectToAction("Index");
            }

        }
        public async Task<IActionResult> DeletePostAsync(int id)
        {
            try
            {
                int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                var role = await _unitOfWork.Role.getRoleName(id);
                int status = await _unitOfWork.Role.DeleteRole(id, user);

                if (status == 1)
                {
                    string? Sucess_message = LangResource.Resource.Roles + role.FirstOrDefault().role_name + LangResource.Resource.Deletemsg;
                    string? Activity_title = LangResource.Resource.RolesDelete;
                    await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                    TempData["success"] = "Role Deleted successfully !";
                    TempData["Valid"] = "1";
                }
                else
                {
                    TempData["success"] = "Failed to delete role !";
                    TempData["Valid"] = "0";
                }
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = LangResource.Resource.RolesDelete;
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index");
            }
        }

        public async Task<ActionResult> IsRoleExisting(string role)
        {
            try
            {
                var data = await _unitOfWork.Role.getRoleNameByName(role);
                if (data == null || data.Count() == 0)
                {
                    return Json("false");
                }
                else
                {
                    return Json("true");
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

    }
}
