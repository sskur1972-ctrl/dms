/* For Depot Master Crude Operations
 * Added By Rahul Mudgal
 */
using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Security.Cryptography.Xml;
using System.Text;

namespace DMS.Web.Controllers
{
    public class AuthErrorPageController : Controller
    {

        [HttpGet]
        public IActionResult InternalErrorPage()
        { 

            return View();
        }


        [HttpGet]
        public IActionResult PermissionErrorPage()
        {

            return View();
        }

    }
}
