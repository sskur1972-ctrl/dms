using DMS.DataAccess.Data;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model;
using DMS.Model.Common;
using DMS.Web.Services.Rendition;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class CommonController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IWebHostEnvironment _hostEnvironment;
        private readonly EuroOfficePdfConverter _pdfConverter;

        public CommonController(
            IUnitOfWork db,
            IWebHostEnvironment hostEnvironment,
            EuroOfficePdfConverter pdfConverter)
        {
            _unitOfWork = db;
            _hostEnvironment = hostEnvironment;
            _pdfConverter = pdfConverter;
        }

        public async Task<ActionResult> viewDocument(IFormFile file, CancellationToken ct)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return Json(false);

                cpe_common_docs _Common_Docs = new cpe_common_docs();

                double fileSizeInKB = file.Length / 1024.0;
                string wwwRootPath = _hostEnvironment.WebRootPath;

                var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
                var DocType = fileExtension.Remove(0, 1);
                string subPath = Path.Combine(wwwRootPath, "UploadFiles", "CommontmpDocument");

                if (!Directory.Exists(subPath))
                    Directory.CreateDirectory(subPath);

                var uniqueFileName = Guid.NewGuid().ToString() + fileExtension;
                var filePath = Path.Combine(subPath, uniqueFileName);

                if (fileExtension == ".pdf")
                {
                    await using var pdfStream = file.OpenReadStream();
                    byte[] docxBytes = await _pdfConverter.ConvertToDocxAsync(pdfStream, file.FileName, 60, ct);

                    uniqueFileName = Path.ChangeExtension(uniqueFileName, ".docx");
                    filePath = Path.Combine(subPath, uniqueFileName);
                    await System.IO.File.WriteAllBytesAsync(filePath, docxBytes, ct);

                    DocType = "docx";
                    fileSizeInKB = docxBytes.Length / 1024.0;
                    _Common_Docs.userFileName = Path.ChangeExtension(file.FileName, ".docx");
                }
                else
                {
                    await using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream, ct);
                    }
                    _Common_Docs.userFileName = file.FileName;
                }

                ViewBag.Message = "File uploaded successfully!";

                _Common_Docs.createdOn = DateTime.Now;
                _Common_Docs.systemFileName = uniqueFileName;
                _Common_Docs.fileType = DocType;
                _Common_Docs.filePath = filePath;
                _Common_Docs.fileSize = fileSizeInKB;

                return Json(_Common_Docs);
            }
            catch
            {
                return Json(false);
            }
        }

        internal Task<string> GetNextUserName(string v1, string v2, string permissionList, List<AppSettings> obj_appSettings)
        {
            throw new NotImplementedException();
        }

        [HttpGet]
        public IActionResult ViewUploadedDocument(string filePath)
        {
            if (System.IO.File.Exists(filePath))
            {
                var fileBytes = System.IO.File.ReadAllBytes(filePath);
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
                string contentType = string.Empty;

                if (fileExtension == ".pdf")
                {
                    contentType = "application/pdf";
                }
                else if (fileExtension == ".jpeg" || fileExtension == ".jpg" || fileExtension == ".png")
                {
                    contentType = "image/jpeg";
                }
                else if (fileExtension == ".doc")
                {
                    contentType = "application/msword";
                }
                else if (fileExtension == ".docx")
                {
                    contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                }
                else if (fileExtension == ".xls")
                {
                    contentType = "application/vnd.ms-excel";
                }
                else if (fileExtension == ".xlsx")
                {
                    contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                }

                if (fileExtension == ".xls" || fileExtension == ".xlsx")
                {
                    return File(fileBytes, contentType, Path.GetFileName(filePath));
                }
                else
                {
                    return File(fileBytes, contentType);
                }
            }
            return NotFound();
        }
    }
}
