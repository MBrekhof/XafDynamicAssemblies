using DevExpress.ExpressApp;
using DevExpress.ExpressApp.SystemModule;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Controllers
{
    /// <summary>
    /// Intercepts navigation to <c>AIChat_ListView</c> and redirects to the DetailView
    /// which hosts the DxAIChat component.
    /// </summary>
    public class ShowAIChatController : WindowController
    {
        public ShowAIChatController()
        {
            TargetWindowType = WindowType.Main;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            var navController = Frame.GetController<ShowNavigationItemController>();
            if (navController != null)
                navController.CustomShowNavigationItem += OnCustomShowNavigationItem;
        }

        protected override void OnDeactivated()
        {
            var navController = Frame.GetController<ShowNavigationItemController>();
            if (navController != null)
                navController.CustomShowNavigationItem -= OnCustomShowNavigationItem;
            base.OnDeactivated();
        }

        private void OnCustomShowNavigationItem(object sender, CustomShowNavigationItemEventArgs e)
        {
            if (e.ActionArguments.SelectedChoiceActionItem?.Data is ViewShortcut shortcut
                && shortcut.ViewId == "AIChat_ListView")
            {
                var objectSpace = Application.CreateObjectSpace(typeof(AIChat));
                var chatObject = objectSpace.CreateObject<AIChat>();
                var detailView = Application.CreateDetailView(objectSpace, chatObject);
                detailView.ViewEditMode = DevExpress.ExpressApp.Editors.ViewEditMode.View;
                e.ActionArguments.ShowViewParameters.CreatedView = detailView;
                e.Handled = true;
            }
        }
    }
}
