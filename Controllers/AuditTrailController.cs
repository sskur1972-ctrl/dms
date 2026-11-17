using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Common;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace DMS.Web.Controllers
{
    public class AuditTrailController : Controller
    {
        private readonly ApplicationDbContext _dbContext;

        private readonly IUnitOfWork _unitOfWork;
        public string _permissionList = string.Empty;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IWebHostEnvironment _hostEnvironment;
        public AuditTrailController(IUnitOfWork db, IWebHostEnvironment hostEnvironment, IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext)
        {
            _unitOfWork = db;
            _httpContextAccessor = httpContextAccessor;

            _hostEnvironment = hostEnvironment;
            _permissionList = _httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "serializedPermissionList")?.Value;

            _dbContext = dbContext;
        }

        public async Task<IActionResult> Index(string From_date, string To_date, int? Id)
        {
            try
            {
                ViewBag.usersList = await _unitOfWork.AuditTrailService.GetUsersList();
                ViewBag.roleList = await _unitOfWork.Role.GetAllRoleListAsync();
                int Userid = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                int effectiveId = Id ?? Userid;

                string? adjustedFromDate = string.IsNullOrEmpty(From_date) ? null : DateTime.Parse(From_date).ToString("yyyy-MM-dd");
                string? adjustedToDate = string.IsNullOrEmpty(To_date) ? null : DateTime.Parse(To_date).ToString("yyyy-MM-dd");
                ViewBag.fromdate = adjustedFromDate;
                ViewBag.toDate = adjustedToDate;

                var autTrailLists = await _unitOfWork.AuditTrailService.GetList(effectiveId, adjustedFromDate, adjustedToDate);
                if (autTrailLists == null || !autTrailLists.Any())
                {
                    throw new Exception("Data Not Found");
                }
                return View(autTrailLists);
            }
            catch (Exception ex)
            {
                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";
                return View(new List<AuditTrailViewModel>());
            }
        }

        public async Task<IActionResult> ErrorIndex(string From_date, string To_date, int Id)
        {
            try
            {
                if (_permissionList != null)
                {

                    if (_permissionList.Contains("\"View Error index\""))

                    {
                        ViewBag.usersList = await _unitOfWork.AuditTrailService.GetUsersList();

                        int Userid = Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                        DateTime fromDate = string.IsNullOrEmpty(From_date) ? DateTime.Today : DateTime.Parse(From_date);
                        DateTime toDate = string.IsNullOrEmpty(To_date) ? DateTime.Today : DateTime.Parse(To_date);

                        // Format dates as yyyy-MM-dd
                        string adjustedFromDate = fromDate.ToString("yyyy-MM-dd");
                        string adjustedToDate = toDate.ToString("yyyy-MM-dd");

                        // Set to ViewBag for use in the view
                        ViewBag.fromdate = adjustedFromDate;
                        ViewBag.toDate = adjustedToDate;
                        if (From_date != null && To_date != null)
                        {
                            var autTrailLists = await _unitOfWork.AuditTrailService.GetErrorList(Id, From_date, To_date);
                            if (autTrailLists == null || autTrailLists.Count() == 0)
                            {
                                throw new Exception("Data Not Found");
                            }
                            return View(autTrailLists);
                        }
                        else
                        {

                            var autTrailLists = await _unitOfWork.AuditTrailService.GetErrorList(Id, null, null);
                            if (autTrailLists == null || autTrailLists.Count() == 0)
                            {
                                throw new Exception("Data Not Found");
                            }
                            return View(autTrailLists);

                        }



                        //return View(new List<AuditErrorlViewModel>());
                    }
                }


                return RedirectToAction("PermissionErrorPage", "AuthErrorPage", new { cname = ControllerContext.RouteData.Values["controller"].ToString(), aname = ControllerContext.ActionDescriptor.ActionName });

            }
            catch (Exception ex)
            {

                TempData["success"] = ex.Message;
                TempData["Valid"] = "0";

                return View(new List<AuditErrorlViewModel>());
            }



        }

        public async Task AddLogAsyncByID(int _TableID, string ActivityId, string description)
        {
            try
            {

                //var activityData = await _unitOfWork.AuditTrailService.GetActivityByName(ActivityId);
                //if (activityData.Count() == 0)
                //{
                //    throw new Exception("activityData not found");
                //}
                int userid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);
                int roleid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "roles")?.Value);
                string ipAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();

                if (ipAddress == "::1")
                {
                    ipAddress = Dns.GetHostEntry(Dns.GetHostName()).AddressList[1].ToString();
                }
                ViewBag.IP = ipAddress.ToString();


                var results = await _unitOfWork.AuditTrailService.AddAuditTrail(roleid, userid, _TableID, 0, description, 1, ipAddress, ActivityId.FirstOrDefault());
                //var results = await _unitOfWork.AuditTrailService.AddAuditTrail(Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "RoleId")?.Value), Convert.ToInt32(User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value), tableData[0].pk_TableName, 0, description, 1, ipAddress, activityData[0].ActivityId);
                if (results < 0)
                {
                    throw new Exception("Table name not found");
                }
            }
            catch (Exception ex)
            {

                //var objs = await _unitOfWork.Common_Trail.AddErrorTrailAsync(ex.Message, Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value), "Document Delete", Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "RoleId")?.Value));
                TempData["success"] = "Failed to proce";
                TempData["Valid"] = "0";
                //return RedirectToAction("Index");
            }

        }


        public async Task AddSucessAuditTrail(string Sucess_message, string Activity)
        {
            try
            {
                int userid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                int roleid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "roles")?.Value);
                string? ipAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();

                if (ipAddress == "::1")
                {
                    ipAddress = Dns.GetHostEntry(Dns.GetHostName()).AddressList[1].ToString();
                }

                ViewBag.IP = new string[]
                {
                    ipAddress.ToString()
                };

                var results = await _unitOfWork.AuditTrailService.AddTrailAsync(Sucess_message, userid, Activity, roleid, ipAddress);

            }
            catch
            {

            }
        }

        public async Task AddErrorAuditTrail(string error_message, string Activity)
        {
            try
            {
                int userid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserID")?.Value);

                int roleid = Convert.ToInt32(_httpContextAccessor.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "roles")?.Value);

                string? ipAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();

                if (ipAddress == "::1")
                {
                    ipAddress = Dns.GetHostEntry(Dns.GetHostName()).AddressList[1].ToString();
                }

                ViewBag.IP = new string[]
                {
                    ipAddress.ToString()
                };

                var error_trail = await _unitOfWork.AuditTrailService.AddErrorTrailAsync(error_message, userid, Activity, roleid, ipAddress);

            }
            catch
            {

            }
        }
    }
}
