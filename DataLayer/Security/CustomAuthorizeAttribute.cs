using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Excise.Web.DataLayer.Security
{
    public class CustomAuthorizeAttribute : IAuthorizationRequirement
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        public CustomAuthorizeAttribute(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;

        }
        
        public string GetUserId()
        {
            var user = _httpContextAccessor?.HttpContext?.User as ClaimsPrincipal;
            var userId = user.Claims.ElementAt(0).Value;
            return userId;
        }
        //public void getPermissions(int RoleId)
        //{
        //    ApplicationDbContext db = new ApplicationDbContext();
        //    var permissions = db.role_permission.Where(x => x.Role_Id == RoleId).Select(x => x.Permissions.Permission).ToList();

        //}
    }
}

