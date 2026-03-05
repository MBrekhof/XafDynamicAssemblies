using System.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using XafDynamicAssemblies.Module.BusinessObjects;
using XafDynamicAssemblies.Module.Services;

namespace XafDynamicAssemblies.Module.Controllers
{
    public class TestCompileController : ObjectViewController<ListView, CustomClass>
    {
        private readonly SimpleAction _testCompileAction;

        public TestCompileController()
        {
            _testCompileAction = new SimpleAction(this, "TestCompile", "Edit")
            {
                Caption = "Test Compile All",
                ToolTip = "Compile all runtime class definitions to check for errors without loading them.",
                ImageName = "Action_Debug_Start",
                ConfirmationMessage = null,
                SelectionDependencyType = SelectionDependencyType.Independent
            };
            _testCompileAction.Execute += TestCompile_Execute;
        }

        private void TestCompile_Execute(object sender, SimpleActionExecuteEventArgs e)
        {
            var allClasses = ObjectSpace.GetObjectsQuery<CustomClass>()
                .Where(cc => cc.Status == CustomClassStatus.Runtime)
                .ToList();

            if (allClasses.Count == 0)
            {
                Application.ShowViewStrategy.ShowMessage(
                    "No runtime classes to compile.",
                    InformationType.Warning);
                return;
            }

            var result = RuntimeAssemblyBuilder.ValidateCompilation(allClasses);

            if (result.Success)
            {
                var msg = $"Test compilation successful! {allClasses.Count} runtime class(es) compiled without errors.";
                if (result.Warnings.Count > 0)
                    msg += $"\n\nWarnings ({result.Warnings.Count}):\n" + string.Join("\n", result.Warnings);

                Application.ShowViewStrategy.ShowMessage(msg, InformationType.Success);
            }
            else
            {
                var msg = "Compilation failed:\n" + string.Join("\n", result.Errors);

                Application.ShowViewStrategy.ShowMessage(msg, InformationType.Error);
            }
        }
    }
}
