
using Excise.Model;
using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;

namespace Excise.Web.DataLayer.Security
{
    public class SecurityUtils
    {
        private ApplicationDbContext db = new ApplicationDbContext();
        private readonly IUnitOfWork _unitOfWork;
        public SecurityUtils(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        //public List<string> getPermissions(int RoleId)
        //{
           

        //   return db.role_permission.Where(x => x.Role_Id == RoleId).Select(x => x.Permissions.Permission).ToList(); ;
        //}

        //public List<Permissions> getPermissionsList()
        //{
        //    return db.permissions.Where(x => x.IsActive == true).ToList();
        //    //return db.ROLESPermission.Where(x => x.Roles_Id == RoleId).Select(x => x.Permissions ).Distinct().ToList();
        //}

        //public string getRoleName(int? RoleId)
        //{
        //    return db.auth_roles.Where(x => x.RolesID == RoleId).Select(x => x.Roles_Name).FirstOrDefault();

        //}
       
    }
}
