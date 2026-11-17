///Added by Prathamesh Patil on 28th Feb 2025
/// <summary>
/// The AccountController class handles user authentication, password management, and related functionalities.
/// It provides actions for user login, logout, password reset, and password change, as well as managing
/// user sessions and authentication cookies. This controller integrates with the application's data access
/// layer and audit trail system to ensure secure and traceable user interactions.
/// </summary>
/// <remarks>
/// Key functionalities include:
/// - User login and authentication using encrypted passwords.
/// - Password reset and change operations with email notifications.
/// - Session management and user claims for role-based access control.
/// - Integration with the audit trail system for tracking user activities.
/// </remarks>

using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model;
using DMS.Model.Account;
using DMS.Model.Common;
using DMS.Web.CommonUtils;
using DMS.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using PTPLCRYPTORENGINE;
using System.Data;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;

namespace Excise.Web.Controllers
{
    public class AccountController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ApplicationDbContext _dbContext;
        private readonly IWebHostEnvironment _hostEnvironment;
        public AuditTrailController auditTrailController;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        List<AppSettings> _appSettings = SharedProperties.Instance._appsettings;

        public AccountController(IUnitOfWork unitOfWork, IHttpContextAccessor httpContextAccessor,
            ApplicationDbContext dbContext, IWebHostEnvironment hostEnvironment, IConfiguration config)
        {
            _unitOfWork = unitOfWork;
            _hostEnvironment = hostEnvironment;
            _httpContextAccessor = httpContextAccessor;
            _dbContext = dbContext;
            auditTrailController = new AuditTrailController(_unitOfWork, _hostEnvironment, _httpContextAccessor, _dbContext);
            _config = config;
        }

        public IActionResult Index()
        {
            return View();
        }

        private PasswordCipher PasswordCrypto => HttpContext.RequestServices.GetRequiredService<PasswordCipher>();

        private bool StandaloneMode =>
            !string.Equals(_config["SharedAuthentication:Mode"], "Portal", StringComparison.OrdinalIgnoreCase);

        [AllowAnonymous]
        public IActionResult Login(string? controllers, string? returnUrl = null)
        {
            if (StandaloneMode) { ViewBag.ReturnUrl = returnUrl; return View(); }
            var returnTarget = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
                ? $"{Request.Scheme}://{Request.Host}{returnUrl}"
                : Url.Action("Index", "Home", null, Request.Scheme)!;
            return Redirect(DMS.Web.Services.SharedAuthenticationSettings.LoginUrl(_config, returnTarget));
        }


        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Login(LoginViewModel model)
        {
            if (!StandaloneMode)
            {
                var target = Url.Action("Index", "Home", null, Request.Scheme)!;
                return Redirect(DMS.Web.Services.SharedAuthenticationSettings.LoginUrl(_config, target));
            }
            try
            {


                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(x => x.Errors).Select(x => x.ErrorMessage);
                    foreach (var error in errors)
                    {
                        await auditTrailController.AddErrorAuditTrail(error, "User login details");
                    }
                    TempData["Success"] = "Please Provide Login Details.";
                    TempData["Valid"] = "0";
                    return RedirectToAction("Login");
                }

                var appSettings = await _unitOfWork.AppSetting.GetSettingList();
                string? ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                string? userAgent = Request.Headers.UserAgent.ToString();

                // 1. the account (user name or e-mail)
                var userAccount = await _unitOfWork.UserService.GetUserForLogin(model.username);

                if (userAccount != null)
                {
                    // Shared administration is authoritative for account status.
                    string? refusal = null;
                    if (userAccount.isactive != true) refusal = "Account is deactivated";

                    if (refusal != null)
                    {
                        await auditTrailController.AddErrorAuditTrail($"{refusal}: {model.username}", "User login details");
                        TempData["Success"] = refusal + ".";
                        TempData["Valid"] = "0";
                        return RedirectToAction("Login");
                    }


                    // Shared common credentials use PTPL AES encryption.
                    if (!PasswordCrypto.Verify(model.Password, userAccount.password))
                    {
                        await auditTrailController.AddErrorAuditTrail("Invalid user login details", "User login details");
                        TempData["Success"] = "Invalid user login details.";
                        TempData["Valid"] = "0";
                        return RedirectToAction("Login");
                    }

                    if (userAccount.must_change_password)
                    {
                        TempData["Success"] = "Complete your first-login password setup in the shared administration module.";
                        TempData["Valid"] = "0";
                        return RedirectToAction("Login");
                    }
                    var currentRole = (await _unitOfWork.Role.getRoleName(userAccount.role_id)).ToList();


                    if (currentRole != null && currentRole.Any())
                    {
                        _unitOfWork.UserService.CreateAuthenticationCookie(userAccount);

                        // The primary role and grants come from common.
                        var userPermissions = await _unitOfWork.Role.getPermissionsForUser(userAccount.pk_user);
                        var serializedPermissionList = JsonConvert.SerializeObject(userPermissions, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                        List<UserNotificationViewMode> userNotifications = new List<UserNotificationViewMode>();
                        var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, userAccount.emailid ?? userAccount.username ?? ""),
                        new Claim("displayName", userAccount.DisplayName ?? userAccount.username ?? ""),
                        new Claim("role_name", currentRole.First().role_name ?? ""),
                        new Claim("UserID", userAccount.pk_user.ToString()),
                        new Claim("IdentitySource", "common-v1"),
                        new Claim("roles", userAccount.role_id.ToString() ?? ""),
                        new Claim("DepartmentId", (userAccount.fk_department_id ?? 0).ToString()),
                        new Claim("PlantId", (userAccount.plant_id ?? 0).ToString()),
                        new Claim("MustChangePassword", userAccount.must_change_password ? "1" : "0"),
                        new Claim("serializedPermissionList", serializedPermissionList),
                        new Claim("serializedNotification", JsonConvert.SerializeObject(userNotifications, Formatting.Indented)),
                    };

                        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                        var principal = new ClaimsPrincipal(identity);

                        SetAppConfig(appSettings, userPermissions.ToList());
                        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                        HttpContext.User = principal;   // so the audit row carries this user

                        string? Success_message = $"User {userAccount.DisplayName} Login successfully.";
                        string? Activity_title = "User Login";
                        await auditTrailController.AddSucessAuditTrail(Success_message, Activity_title);


                        return RedirectToAction("Index", "Home");
                    }
                }

                await auditTrailController.AddErrorAuditTrail("Invalid user login details", "User login details");
                TempData["Success"] = "Invalid user login details.";
                TempData["Valid"] = "0";

                return RedirectToAction("Login");
            }
            //catch (Exception ex)
            //{
            //    return RedirectToAction("Login");
            //}
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }
        }

        public async Task<ActionResult> LogOut(string? returnUrl = null)
        {
            string username = User.Claims.FirstOrDefault(c => c.Type == "displayName")?.Value;

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            TempData.Clear();
            string? Success_message = $"User {username} Logout successfully.";
            await auditTrailController.AddSucessAuditTrail(Success_message, "User Logout");
            if (!StandaloneMode)
                return Redirect(DMS.Web.Services.SharedAuthenticationSettings.LogoutUrl(_config));
            return RedirectToAction("Login");
        }

        [HttpGet]
        public ActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        public async Task<ActionResult> ForgotPassword(ForgetPassVM obj)
        {
            if (!ModelState.IsValid)
            {
                TempData["error"] = "Please enter a valid email address.";
                return View(obj);
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                await transaction.CreateSavepointAsync("BeforeReset");

                var userData = await _unitOfWork.UserService.GetUserUsingUserNameAndEmail(obj.EmailId);
                var user = userData?.FirstOrDefault();

                if (user == null)
                    throw new Exception("Invalid Email ID. Please enter a valid Email ID!");

                string newPassword = Generatepassword();
                string encryptedPassword = PasswordCrypto.EncryptForStorage(newPassword);

                int updateResult = await _unitOfWork.UserService.ResetPassword(user.pk_user, encryptedPassword);
                if (updateResult <= 0)
                    throw new Exception("Failed to update password.");

                // Load mail settings
                var mailSettings = new MailSettings
                {
                    Host = await _unitOfWork.AppSetting.GetAppSettingValueAsync("SMTPHost"),
                    Port = Int16.Parse(await _unitOfWork.AppSetting.GetAppSettingValueAsync("SMTPPort")),
                    Password = await _unitOfWork.AppSetting.GetAppSettingValueAsync("SMTPPassword"),
                    FromEmail = await _unitOfWork.AppSetting.GetAppSettingValueAsync("SMTPFromEmail"),
                    FromName = await _unitOfWork.AppSetting.GetAppSettingValueAsync("SMTPFromName")
                };

                // Prepare mail request
                var mailRequest = new MailRequest
                {
                    ToEmailAddress = obj.EmailId,
                    ToName = user.DisplayName,
                    Subject = "Reset Password",
                    EmailContent = GeneratePasswordResetEmail(user.DisplayName, newPassword),
                    IsHtml = true
                };

                await SendMailAsync(mailRequest, mailSettings);

                await transaction.CommitAsync();

                await auditTrailController.AddSucessAuditTrail("Password reset email sent successfully", "Forgot Password");
                TempData["success"] = "Password reset email sent successfully!";
                TempData["Valid"] = "1";

                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                await auditTrailController.AddErrorAuditTrail(ex.Message, "Forgot Password");

                TempData["error"] = ex.Message;
                TempData["Valid"] = "0";

                return View(obj); // Keeps user input and shows error
            }
        }


        [NonAction]
        public async Task SendEmailAsync(string to, string subject, string body)
        {
            var settings = new
            {
                Host = _appSettings.Find(s => s.key == "SMTPHost"),
                Port = int.TryParse(_appSettings.FirstOrDefault(s => s.key == "SMTPPort")?.value, out int port) ? port : 587,
                FromEmail = _appSettings.Find(s => s.key == "SMTPFromEmail"),
                FromName = _appSettings.Find(s => s.key == "SMTPFromName"),
                Password = _appSettings.Find(s => s.key == "SMTPPassword")
            };

            var smtpClient = new SmtpClient(settings.Host.value)
            {
                Port = settings.Port,
                EnableSsl = true,
                UseDefaultCredentials = string.IsNullOrEmpty(settings.Password.value), // No password means use default credentials
            };

            if (!string.IsNullOrEmpty(settings.Password.value))
            {
                smtpClient.Credentials = new NetworkCredential(settings.FromEmail.value, settings.Password.value);
            }

            var mailMessage = new MailMessage
            {
                From = new MailAddress(settings.FromEmail.value, settings.FromName.value),
                Subject = subject,
                Body = body,
                IsBodyHtml = true
            };
            mailMessage.To.Add(to);

            await smtpClient.SendMailAsync(mailMessage);
        }

        [HttpGet]
        public async Task<IActionResult> ChangePassword()
        {
            try
            {
                var userId = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                var userDetails = await _unitOfWork.UserService.GetUsersById(userId);

                var userPassword = new UserPassword
                {
                    ID = userDetails.FirstOrDefault().pk_user,
                    UserName = userDetails.FirstOrDefault().username,
                    displayName = userDetails.FirstOrDefault().DisplayName,
                    //Roles_Name = userDetails.FirstOrDefault().role_name,
                    emailid = userDetails.FirstOrDefault().emailid
                };

                return View(userPassword);
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return View(new UserPassword());
            }
        }

        [HttpPost]
        public async Task<IActionResult> ChangePassword(UserPassword obj)
        {
            try
            {
                if (obj == null)
                {
                    throw new Exception("Invalid data provided.");
                }

                // the user can only change his own password
                var loggedInUser = Convert.ToInt64(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                if (loggedInUser != obj.ID)
                {
                    throw new Exception("You can change only your own password.");
                }

                var current = (await _unitOfWork.UserService.GetUsersById((int)obj.ID)).FirstOrDefault()
                              ?? throw new Exception("User not found.");
                if (!PasswordCrypto.Verify(obj.oldpassword, current.password))
                {
                    throw new Exception("Old password is incorrect.");
                }
                if (obj.newpassword != obj.confrimpassword)
                {
                    throw new Exception("New password and confirm password do not match.");
                }

                var policyError = PasswordPolicy.Validate(obj.newpassword, await _unitOfWork.AppSetting.GetSettingList());
                if (policyError != null)
                {
                    throw new Exception(policyError);
                }

                var updateResult = await _unitOfWork.UserService.Updatepassword(obj.ID, PasswordCrypto.EncryptForStorage(obj.newpassword));

                if (updateResult > 0)
                {
                    await auditTrailController.AddSucessAuditTrail($"User '{obj.emailid}' updated successfully", "Change Password");
                    TempData["success"] = $"User '{obj.emailid}' updated successfully !";
                    TempData["Valid"] = "1";
                }
                else
                {
                    await auditTrailController.AddErrorAuditTrail($"User '{obj.emailid}' not updated", "Change Password");
                    TempData["success"] = $"Error: User '{obj.emailid}' not updated !";
                    TempData["Valid"] = "0";
                }

                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";

                await auditTrailController.AddErrorAuditTrail(ex.Message, "Change Password");
                return View(obj);
            }
        }

        private async Task SendMailAsync(MailRequest request, MailSettings settings)
        {
            try
            {
                var mailMsg = new MailMessage
                {
                    From = new MailAddress(settings.FromEmail, settings.FromName),
                    Subject = request.Subject,
                    Body = request.EmailContent,
                    IsBodyHtml = request.IsHtml
                };

                mailMsg.To.Add(new MailAddress(request.ToEmailAddress, request.ToName));

                using (var smtpClient = new SmtpClient(settings.Host, settings.Port))
                {
                    smtpClient.UseDefaultCredentials = false;
                    smtpClient.Credentials = new NetworkCredential(settings.FromEmail, settings.Password);
                    smtpClient.EnableSsl = true;
                    await smtpClient.SendMailAsync(mailMsg);
                }
            }
            catch (Exception ex)
            {
                // Log the exception (e.g., using a logging framework)
                throw new Exception("Failed to send email.", ex);
            }
        }

        private string GeneratePasswordResetEmail(string displayName, string newPassword)
        {
            return $@"
        <div style='font-family: Arial, sans-serif; padding: 20px;'>
            <h2>Password Reset</h2>
            <p>Hello {displayName},</p>
            <p>Your password has been reset successfully. Below is your new password:</p>
            <p><strong>Password:</strong> {newPassword}</p>
            <p>Thank you,</p>
            <p>Your Application Team</p>
        </div>";
        }

        private void SetAppConfig(List<AppSettings> appSettingValue, List<RolePermissionViewModel> userPermissions)
        {
            SharedProperties.Instance._appsettings = appSettingValue;
            SharedProperties.Instance.currentPermmision = userPermissions;
        }

        private string Generatepassword()
        {
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            var random = new Random();
            return new string(Enumerable.Repeat(validChars, 8)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        [HttpPost]
        public async Task<ActionResult> ConfrimOldpassword(int Id, string oldpassword)
        {
            var status = await _unitOfWork.UserService.GetUsersById(Id);

            if (!PasswordCrypto.Verify(oldpassword, status.FirstOrDefault()?.password))
            {
                return Json(new { value = "empty" });

            }
            else
            {
                return Json(new { value = "true" });
            }

        }

        public static string GenerateOtp(int length = 6)
        {
            const string digits = "0123456789";
            var random = new Random();
            var otp = new string(Enumerable.Repeat(digits, 6)
                .Select(s => s[random.Next(s.Length)]).ToArray());

            return new string(otp);
        }


        private string SendOTPOnEmail(string displayName, string OTP)
        {
            return $@"
                   <div class=""container"">
                        <p>Dear <strong>{displayName}</strong>,</p>
                        <p>Your One-Time Password (OTP) for login is:</p>
                        <div class=""otp-code""><strong>{OTP}</strong></div>
                        <p>Please do not share this code with anyone for security reasons.</p>
                        <p>If you did not request this OTP, please ignore this email or contact our support team immediately.</p>
                        <div class=""footer"">
                          Thank you,<br>
                          <strong>Your Application Team</strong>
                        </div>
                  </div>
                    ";
        }

        [HttpGet]
        public async Task<IActionResult> NotificationIndex()
        {
            try
            {
                var userId = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                var status = await _unitOfWork.UserService.GetUserNotificationById(userId);
                return View(status);
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return View(new List<UserNotificationViewModel>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> NotificationDetailsView(int id)
        {
            try
            {
                var updatestatusisviewd = await _unitOfWork.UserService.UpdateNotificationStatus(id);
                var status = await _unitOfWork.UserService.GetNotificationDetails(id);

                return View(status.FirstOrDefault());
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return View(new List<UserNotificationViewModel>());
            }
        }
        private void CreateStudentCookie(HttpContext context, string otp)
        {
            CookieOptions options = new CookieOptions
            {
                Expires = DateTime.Now.AddMinutes(5)
            };

            context.Response.Cookies.Append("AuthOTP", otp, options);
        }

        [HttpPost]
        public async Task<IActionResult> MarkNotificationViewed(int id)
        {
            try
            {
                var result = await _unitOfWork.UserService.UpdateNotificationStatus(id);
                return Json(new { success = result > 0 });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> MarkNotificationViewedByReference(int jobId)
        {
            try
            {
                var notificationId = await _unitOfWork.UserService.MarkNotificationViewedByReferenceAsync(jobId);
                return Json(new { success = notificationId > 0, notificationId });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }



    }
}
