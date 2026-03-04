using System.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    public class TestCompileController : ObjectViewController<DetailView, CustomClass>
    {
        private readonly SimpleAction _testCompileAction;

        public TestCompileController()
        {
            _testCompileAction = new SimpleAction(this, "TestCompile", "Edit")
            {
                Caption = "Test Compile",
                ToolTip = "Compile this class definition to check for errors without loading it.",
                ImageName = "Action_Debug_Start",
                ConfirmationMessage = null
            };
            _testCompileAction.Execute += TestCompile_Execute;
        }

        private void TestCompile_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var customClass = (CustomClass)View.CurrentObject;

            if (string.IsNullOrWhiteSpace(customClass.ClassName))
            {
                Application.ShowViewStrategy.ShowMessage(
                    "Cannot compile: Class Name is empty.",
                    InformationType.Error);
                return;
            }

            // Include all runtime classes so cross-references resolve correctly
            var allClasses = ObjectSpace.GetObjectsQuery<CustomClass>()
                .Where(cc => cc.Status == CustomClassStatus.Runtime)
                .ToList();

            // Ensure the current (possibly unsaved) object is in the list
            if (!allClasses.Any(cc => cc.ID == customClass.ID))
                allClasses.Add(customClass);

            var result = RuntimeAssemblyBuilder.ValidateCompilation(allClasses);

            if (result.Success)
            {
                var msg = $"Test compilation successful! Class '{customClass.ClassName}' compiled without errors.";
                if (result.Warnings.Count > 0)
                    msg += $"\n\nWarnings ({result.Warnings.Count}):\n" + string.Join("\n", result.Warnings);

                Application.ShowViewStrategy.ShowMessage(msg, InformationType.Success);
            }
            else
            {
                var msg = $"Compilation failed for '{customClass.ClassName}':\n" +
                          string.Join("\n", result.Errors);

                Application.ShowViewStrategy.ShowMessage(msg, InformationType.Error);
            }
        }
    }
}
