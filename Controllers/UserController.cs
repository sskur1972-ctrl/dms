using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PTPLCRYPTORENGINE;
using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Account;
using DMS.Web.Controllers;
using DMS.Model.Common;
using DMS.Web.CommonUtils;
using System.Net.Mail;
using System.Net;
using System.Net.Mime;

namespace DMS.Controllers
{
    public class UserController : Controller
    {

        private readonly IUnitOfWork _unitOfWork;
        private readonly ApplicationDbContext _dbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;
        public AuditTrailController auditTrailController;
        private IWebHostEnvironment _hostEnvironment;
        public string _permissionList = string.Empty;
        private PasswordCipher PasswordCrypto => HttpContext.RequestServices.GetRequiredService<PasswordCipher>();

        public UserController(IUnitOfWork unitOfWork, ApplicationDbContext dbContext, IHttpContextAccessor httpContextAccessor, IWebHostEnvironment hostEnvironment)
        {
            _unitOfWork = unitOfWork;
            _dbContext = dbContext;
            _httpContextAccessor = httpContextAccessor;
            _hostEnvironment = hostEnvironment;
            _permissionList = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value;

            auditTrailController = new AuditTrailController(_unitOfWork, _hostEnvironment, _httpContextAccessor, _dbContext);

        }

        public async Task<ActionResult> Index()
        {
            try
            {
                if (_permissionList != null)
                {

                    if (_permissionList.Contains("\"View User\""))

                    {

                        List<UserforIndexmodel> data = await _unitOfWork.UserService.GetUserListForIndex();

                        if (data.Count() == 0 || data == null)
                        {
                            data = new List<UserforIndexmodel>();
                        }

                        string? Sucess_message = "View User Index";
                        string? Activity_title = "User Index";
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        return View(data);
                    }
                }
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");

            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = "User Index";
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.StackTrace.ToString();
                TempData["Valid"] = "0";
                return View();
            }
        }
        [HttpGet]
        public async Task<ActionResult> Create(int? id)
        {
            if (_permissionList != null)
            {

                if (_permissionList.Contains("\"Create User\""))
                {

                    List<RoleDropDownViewModel> totalRoles = await _unitOfWork.Role.GetRoleListForDropDown();
                    if (totalRoles == null || totalRoles.Count() == 0)
                    { throw new Exception("Please Add Roles"); }

                    ViewBag.RoleList = totalRoles;
                    ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();

                
                    string? Sucess_message = "Data Fetch For Create User";
                    string? Activity_title = "User Create";
                    await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                    return View();
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");

        }
        [HttpPost]
        public async Task<ActionResult<int>> Create(users member)
        {
           
                try
                {
                    var policyError = PasswordPolicy.Validate(member.password, await _unitOfWork.AppSetting.GetSettingList());
                    if (policyError != null)
                    {
                        TempData["success"] = policyError;
                        TempData["Valid"] = "0";
                        return RedirectToAction("Create", "User");
                    }

                    // Shared common credentials use PTPL AES encryption.
                    var encryptesd_pass = PasswordCrypto.EncryptForStorage(member.password);
                    int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                    var result = await _unitOfWork.UserService.AddUserAsync(member, encryptesd_pass, user);
                    

                    TempData["success"] = DMS.Web.LangResource.Resource.AddUserMsg;
                    TempData["Valid"] = "1";
                    return RedirectToAction("Index", "User");
                    //if (result == "1")
                    //{
                    //    MailSettings settings = new MailSettings();


                    //    settings.Host = await _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPHost"); //settings.Host = _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPHost"); //MyReader.GetValue("Host", typeof(string)).ToString();

                    //    var a = await _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPPort");
                    //    settings.Port = Int16.Parse(a);  //MyReader.GetValue("Port", typeof(string)).ToString();
                    //    settings.Password = await _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPPassword");  //MyReader.GetValue("SMTPPass", typeof(string)).ToString();
                    //    settings.FromEmail = await _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPFromEmail");//MyReader.GetValue("SMTPFromEmail", typeof(string)).ToString();
                    //    settings.FromName = await _unitOfWork.AppSetting.getAppSetingsFromDB("SMTPFromName");//MyReader.GetValue("SMTPFromEmail", typeof(string)).ToString();
                    //    string emailId = string.Empty;
                    //    try
                    //    {
                    //        MailRequest mailRequest = new MailRequest();
                    //        mailRequest.IsHtml = true;
                    //        emailId = member.emailid != null ? member.emailid : string.Empty;
                    //        mailRequest.ToEmailAddress = emailId;

                    //        mailRequest.EmailContent = @"
                    //        <div style='font-family: Roboto, Arial, sans-serif; max-width: 600px; margin: 0 auto; border: 1px solid #e0e0e0; border-radius: 6px; box-shadow: 0 3px 15px rgba(30,32,37,.06); background: #fff;'>
                    //            <!-- Header -->
                    //            <div style='background-color: #0056b3; padding: 15px; text-align: center; border-radius: 6px 6px 0 0;'>
                    //                <h2 style='color:#fff; margin:0; font-size:18px;'>User Account Created</h2>
                    //            </div>

                    //            <!-- Body -->
                    //            <div style='padding: 25px; color: #333; font-size: 14px;'>
                    //                <p>Hi <b>" + member.DisplayName + @"</b>,</p>

                    //                        <p>Your <b>user account</b> has been successfully <b>created</b>.<br/>
                    //                                Below are your login credentials:</p>


                    //                <!-- Credentials box -->
                    //                <div style='border: 1px solid #dcdcdc; background-color: #f9f9f9; padding: 15px; border-radius: 5px; margin: 20px 0;'>
                    //                    <p style='margin: 5px 0; font-size: 15px;'><b style='color:#d9534f;'>User ID (Email):</b> " + member.emailid + @"</p>
                    //                    <p style='margin: 5px 0; font-size: 15px;'><b style='color:#5cb85c;'>Password:</b> " + member.password + @"</p>
                    //                </div>

                    //                <p>Please use these credentials to log in EAMS. 
                    //                We recommend changing your password after your first login.</p>

                    //                    </div>

                    //                    <!-- Footer Links -->
                    //                    <div style='text-align: center; margin: 0px auto; padding-bottom: 20px;'>
                    //                        <ul style='list-style: none; display: flex; justify-content: space-evenly; padding-top: 25px; padding-left: 0px; margin-bottom: 20px; font-family: Roboto, sans-serif;'>
                    //                            <li><a href='#' style='color: #495057;'>Help Center</a></li>
                    //                            <li><a href='#' style='color: #495057;'>Support 24/7</a></li>
                    //                            <li><a href='#' style='color: #495057;'>Account</a></li>
                    //                        </ul>
                    //                        <p style='font-family: Roboto, sans-serif; font-size: 14px; color: #98a6ad; margin: 0px;'>Propix Technologies Pvt Ltd</p>
                    //                    </div>
                    //                </div>";







                    //        mailRequest.Subject = "User Registration";

                    //        mailRequest.ToName = member.DisplayName;


                    //        sendMail(mailRequest, settings);


                    //    }
                    //    catch
                    //    {

                    //    }

                    //    string? Sucess_message = DMS.Web.LangResource.Resource.User + " \"" + member.DisplayName + "\" " + DMS.Web.LangResource.Resource.creation;
                    //    string? Activity_title = "Create User";
                    //    await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                    //   await transaction.CommitAsync();

                    //    TempData["success"] = DMS.Web.LangResource.Resource.AddUserMsg;
                    //    TempData["Valid"] = "1";
                    //    return RedirectToAction("Index", "User");
                    //}
                    //else
                    //{
                    //    throw new Exception("some error occurred user not created");
                    //}
                }
                catch (Exception ex)
                {
                  
                    string? Sucess_message = ex.StackTrace.ToString();
                    string? Activity_title = DMS.Web.LangResource.Resource.UsersCreation;
                    await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                    TempData["success"] = ex.Message;
                    TempData["Valid"] = "0";
                    return RedirectToAction("Index");
                }
            
        }
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            if (_permissionList != null)
            {

                if (_permissionList.Contains("\"View User\""))
                {
                    try
                    {
                        if (id == null)
                        {
                            throw new Exception("User Data Not Found");
                        }

                        // Fetch user data asynchronously
                        var data = await _unitOfWork.UserService.GetUsersById(id); // Check if user data is found
                        if (data == null)
                        {
                            throw new Exception("User Data Not Found");
                        }
                      

                        List<RoleDropDownViewModel> totalRoles = await _unitOfWork.Role.GetRoleListForDropDown();
                        if (totalRoles == null || totalRoles.Count() == 0)
                        { throw new Exception("Please Add Roles"); }

                        ViewBag.RoleList = totalRoles;
                        ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();
                    ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();

                        string? Sucess_message = "Data Fetch For View User";
                        string? Activity_title = "View User";
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        // Return view with user data
                        return View(data.FirstOrDefault());

                    }
                    catch (Exception ex)
                    {
                        string? Sucess_message = ex.StackTrace.ToString();
                        string? Activity_title = DMS.Web.LangResource.Resource.UsersDetails;
                        await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                        TempData["success"] = ex.Message;
                        TempData["Valid"] = "0";
                        return RedirectToAction("Index");
                    }
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");

        }
        public async Task<IActionResult> Edit(int id)
        {
            if (_permissionList != null)
            {

                if (_permissionList.Contains("\"Edit User\""))
                {
                    try
                    {
                        // Check if id is null
                        if (id == null)
                        {
                            throw new Exception("User Data Not Found");
                        }

                        // Fetch user data asynchronously
                        var data = await _unitOfWork.UserService.GetUsersById(id);
                        if (data == null)
                        {
                            throw new Exception("User Data Not Found");
                        }
                       
                        List<RoleDropDownViewModel> totalRoles = await _unitOfWork.Role.GetRoleListForDropDown();
                        if (totalRoles == null || totalRoles.Count() == 0)
                        { throw new Exception("Please Add Roles"); }

                        ViewBag.RoleList = totalRoles;
                        ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();
                    ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();

                        string? Sucess_message = "Data Fetch For Edit User";
                        string? Activity_title = "Edit User";
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        return View(data.FirstOrDefault());
                    }
                    catch (Exception ex)
                    {
                        string? Sucess_message = ex.Message;
                        string? Activity_title = "User Create";
                        await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                        TempData["success"] = ex.Message;
                        TempData["Valid"] = "0";
                        return RedirectToAction("Index");
                    }
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult<int>> Edit(users obj)
        {
           
                try
                {
                    int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                    var result = await _unitOfWork.UserService.UpdateUser(obj, user);

                    if (result == "1")
                    {
                        string? Sucess_message = DMS.Web.LangResource.Resource.User + " \"" + obj.DisplayName + "\" " + DMS.Web.LangResource.Resource.Updation;
                        string? Activity_title = DMS.Web.LangResource.Resource.UpdateUserDetails;
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                    

                        TempData["success"] = DMS.Web.LangResource.Resource.UpdateUserMsg;
                        TempData["Valid"] = "1";
                        return RedirectToAction("Index", "User");
                    }
                    else
                    {
                        throw new Exception();
                    }

                }
                catch (Exception ex)
                {
                   
                    string? Sucess_message = ex.StackTrace.ToString();
                    string? Activity_title = DMS.Web.LangResource.Resource.UpdateUserDetails;
                    await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                  
                    TempData["success"] = ex.Message;
                    TempData["Valid"] = "0";
                    return RedirectToAction("Index", "User");
                }

            
        }

        [HttpGet]
        public async Task<IActionResult> Delete(int id)
        {
            if (_permissionList != null)
            {
                if (_permissionList.Contains("\"Delete User\""))
                {
                    try
                    {
                        if (id == null)
                        {
                            throw new Exception("User Data Not Found");
                        }

                        var data = await _unitOfWork.UserService.GetUsersById(id);
                        if (data == null)
                        {
                            throw new Exception("User Data Not Found");
                        }
                      

                        List<RoleDropDownViewModel> totalRoles = await _unitOfWork.Role.GetRoleListForDropDown();
                        if (totalRoles == null || totalRoles.Count() == 0)
                        { throw new Exception("Please Add Roles"); }

                        ViewBag.RoleList = totalRoles;
                        ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();
                    ViewBag.DepartmentList = await _unitOfWork.UserService.GetDepartmentsForDropDown();

                        string? Sucess_message = "Data Fetch For Delete User";
                        string? Activity_title = "Delete User";
                        await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                        return View(data.FirstOrDefault());

                    }
                    catch (Exception ex)
                    {
                        string? Sucess_message = ex.StackTrace.ToString();
                        string? Activity_title = DMS.Web.LangResource.Resource.UsersDetails;
                        await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                        TempData["success"] = ex.Message;
                        TempData["Valid"] = "0";
                        return RedirectToAction("Index");
                    }
                }
            }
            return RedirectToAction("PermissionErrorPage", "AuthErrorPage");

        }

        [HttpPost]
        public async Task<IActionResult> DeletePost(users obj, int? vendorId)
        {
            try
            {

                int user = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                var result = await _unitOfWork.UserService.DeleteUserAsId(obj.pk_user, user);

                if (result == "1")
                {
                    string? Sucess_message = DMS.Web.LangResource.Resource.User + " \"" + obj.DisplayName + "\" " + DMS.Web.LangResource.Resource.Deletemsg;

                    string? Activity_title = DMS.Web.LangResource.Resource.DeleteUser;
                    await auditTrailController.AddSucessAuditTrail(Sucess_message, Activity_title);

                    TempData["success"] = DMS.Web.LangResource.Resource.DelUserMsg;
                    TempData["Valid"] = "1";
                    return RedirectToAction("Index", "User");
                }
                else
                {
                   
                        throw new Exception("Some error occurred user not deleted");
                    
                }

            }
            catch (Exception ex)
            {
                string? Sucess_message = ex.StackTrace.ToString();
                string? Activity_title = DMS.Web.LangResource.Resource.DeleteUser;
                // Add Success audit trail
                await auditTrailController.AddErrorAuditTrail(Sucess_message, Activity_title);

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return RedirectToAction("Index", "User", new { id = 0 });
            }
        }

        // Passwords use PTPL AES encryption with the configured Cipherkey.
        public void sendMail(MailRequest request, MailSettings settings)
        {
            try
            {

                MailMessage mailMsg = new MailMessage();

                mailMsg.To.Add(new MailAddress(request.ToEmailAddress, request.ToName));

                mailMsg.From = new MailAddress(settings.FromEmail, "Propix");

                mailMsg.Subject = request.Subject;
                if (request.IsHtml)
                {
                    string html = request.EmailContent;
                    mailMsg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, null, MediaTypeNames.Text.Html));
                }
                else
                {
                    string text = request.EmailContent;
                    mailMsg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, null, MediaTypeNames.Text.Plain));
                }

                // Init SmtpClient and send
                SmtpClient smtpClient = new SmtpClient(settings.Host, Convert.ToInt32(settings.Port));
                //NetworkCredential credentials = new NetworkCredential(settings.Username, settings.Password);
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(settings.FromEmail, settings.Password);
                smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;
                smtpClient.EnableSsl = true;
                try
                {
                    smtpClient.Send(mailMsg);

                }
                catch (Exception ex)
                {
                    //var error_trail =  _unitOfWork.cpe_TrailService.AddErrorTrailAsync(ex.StackTrace, Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value), "Email sending", Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "RoleId")?.Value));
                    //string? error_message = ex.StackTrace.ToString();
                    //string? Activity_title = "User Creation Mail sending";
                    // AddErrorAuditTrail(error_message, Activity_title);
                    //throw new Exception("Failed To Send Email");
                }

            }
            catch (Exception ex)
            {
                // throw new Exception("");
            }


        }
        public async Task<IActionResult> IsEmailUnique(string email, int userId)
        {
            try
            {

                var data = await _unitOfWork.UserService.CheckEmailExists(email, userId);
                if (data.Any())
                {
                    return Json(true);  // Combination exists
                }
                else
                {
                    return Json(false); // Combination does not exist
                }
            }
            catch (Exception ex)
            {
                return RedirectToAction("PermissionErrorPage", "AuthErrorPage");
            }
        }

    }
}
