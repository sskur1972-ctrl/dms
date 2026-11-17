using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc;

using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace SupplierPortal.Web.CommonUtils
{
    public class RenderPartialViewToString
    {

        public static async Task<string> ViewToStringAsync(Controller controller, string viewName, object model)
        {
            var context = new ActionContext(controller.HttpContext, controller.RouteData, controller.ControllerContext.ActionDescriptor);
           
            using (var sw = new StringWriter())
            {
                IViewEngine viewEngine = controller.HttpContext.RequestServices.GetService(typeof(ICompositeViewEngine)) as ICompositeViewEngine;
                ViewEngineResult viewResult = viewEngine.FindView(context, viewName, false);

                if (!viewResult.Success)
                {
                    throw new InvalidOperationException($"Couldn't find the view {viewName}");
                }

                var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                {
                    Model = model
                };

                var viewContext = new ViewContext(context, viewResult.View, viewData, controller.TempData, sw, new HtmlHelperOptions());
                await viewResult.View.RenderAsync(viewContext);
                return sw.ToString();
            }
        }
    }
}
