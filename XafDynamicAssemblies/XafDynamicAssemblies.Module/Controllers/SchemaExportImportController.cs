using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    public class SchemaExportImportController : ViewController<ListView>
    {
        private readonly SimpleAction _exportAction;
        private readonly SimpleAction _importAction;

        public SchemaExportImportController()
        {
            TargetObjectType = typeof(CustomClass);

            _exportAction = new SimpleAction(this, "ExportSchema", PredefinedCategory.Edit)
            {
                Caption = "Export Schema",
                ImageName = "Action_Export",
                ToolTip = "Export all runtime entity definitions to a JSON file",
            };
            _exportAction.Execute += ExportAction_Execute;

            _importAction = new SimpleAction(this, "ImportSchema", PredefinedCategory.Edit)
            {
                Caption = "Import Schema",
                ImageName = "Action_Import",
                ToolTip = "Import runtime entity definitions from a JSON file",
            };
            _importAction.Execute += ImportAction_Execute;
        }

        private async void ExportAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var fileService = Application.ServiceProvider.GetService(typeof(ISchemaFileService)) as ISchemaFileService;
            if (fileService is null)
                throw new UserFriendlyException("File download is not available in this platform.");

            var dataOs = Application.CreateObjectSpace(typeof(CustomClass));
            var json = SchemaExportImportService.Export(dataOs);
            var fileName = $"schema-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            await fileService.DownloadJsonAsync(fileName, json);

            RecordHistory(SchemaChangeAction.Export, "Schema exported", null, json);
        }

        private async void ImportAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var fileService = Application.ServiceProvider.GetService(typeof(ISchemaFileService)) as ISchemaFileService;
            if (fileService is null)
                throw new UserFriendlyException("File upload is not available in this platform.");

            string json;
            try
            {
                json = await fileService.UploadJsonAsync();
            }
            catch
            {
                return; // user cancelled or error
            }

            if (string.IsNullOrWhiteSpace(json))
                return; // user cancelled the file picker

            var os = Application.CreateObjectSpace(typeof(CustomClass));
            var result = SchemaExportImportService.Import(os, json);

            if (!result.Success)
                throw new UserFriendlyException(result.Message);

            RecordHistory(SchemaChangeAction.Import, result.Message, result.Details, json);

            ObjectSpace.Refresh();
            Application.ShowViewStrategy.ShowMessage(result.Message);
        }

        private void RecordHistory(SchemaChangeAction action, string summary, string details, string json)
        {
            var os = Application.CreateObjectSpace(typeof(SchemaHistory));
            var history = os.CreateObject<SchemaHistory>();
            history.Timestamp = DateTime.UtcNow;
            history.UserName = GetCurrentUserName();
            history.Action = action;
            history.Summary = summary;
            history.Details = details;
            history.SchemaJson = json;
            os.CommitChanges();
        }

        private string GetCurrentUserName() => SecuritySystem.CurrentUser switch
        {
            ISecurityUser user => user.UserName,
            _ => Environment.UserName,
        };
    }
}
