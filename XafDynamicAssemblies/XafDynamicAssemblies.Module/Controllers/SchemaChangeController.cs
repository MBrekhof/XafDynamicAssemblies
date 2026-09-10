using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    /// <summary>
    /// Adds a "Deploy Schema" action to the CustomClass ListView.
    /// Triggers hot-load: DDL sync → Roslyn compile → register types → restart if needed.
    /// </summary>
    public class SchemaChangeController : ViewController<ListView>
    {
        private SimpleAction _deployAction;

        public SchemaChangeController()
        {
            TargetObjectType = typeof(CustomClass);

            _deployAction = new SimpleAction(this, "DeploySchema", PredefinedCategory.Edit)
            {
                Caption = "Deploy Schema",
                ConfirmationMessage = "Deploy all runtime schema changes? The server may briefly restart.",
                ImageName = "Action_Reload",
                ToolTip = "Compile and deploy all runtime entity changes",
            };
            _deployAction.Execute += DeployAction_Execute;
        }

        // async void is the documented XAF Blazor shape for a long-running Execute handler
        // (docs.devexpress.com/eXpressAppFramework/404738); the await resumes on Blazor's
        // synchronization context, so ShowMessage below is safe. On success the process restarts.
        private async void DeployAction_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            string error;
            try
            {
                error = await Task.Run(SchemaChangeOrchestrator.Instance.ExecuteHotLoadAsync);
            }
            catch (Exception ex)
            {
                Tracing.Tracer.LogError($"Deploy schema failed: {ex.Message}");
                error = ex.Message;
            }

            if (error != null && Application != null)
            {
                Application.ShowViewStrategy.ShowMessage(new MessageOptions
                {
                    Message = error,
                    Type = InformationType.Error,
                    Duration = 15000,
                });
            }
        }
    }
}
